using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Services.Umu;

/// <summary>一次原生 umu 启动的计划（与 GameLauncherService.LaunchPlan 字段对齐）。</summary>
public sealed record UmuNativeLaunchPlan(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string ProtonPath,
    SteamRuntimeInfo Runtime,
    ToolManifest Manifest);

/// <summary>
/// 原生 umu 启动器：解析 Proton/Runtime → 准备 prefix → 构建环境 → 经容器 entry-point 启动。
/// 默认不依赖外部 Python umu-run。仅在 Linux 上有意义；Windows 调用直接抛错。
/// </summary>
public sealed class NativeUmuLauncher(
    IProcessRunner processRunner,
    IUmuComponentProvisioner? provisioner = null,
    ILogger? logger = null,
    string? dataHome = null)
{
    /// <summary>
    /// 解析 Proton 路径（绝对目录或版本代号），必要时经 provisioner 下载，
    /// 再解析 toolmanifest 所需 Runtime 并确保就绪。
    /// </summary>
    public async Task<(string ProtonPath, ToolManifest Manifest, SteamRuntimeInfo Runtime)> ResolveComponentsAsync(
        string protonRequest,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureLinux();

        string protonPath;
        if (provisioner is not null)
        {
            protonPath = await provisioner
                .EnsureProtonAsync(protonRequest, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            protonPath = ResolveProtonLocally(protonRequest)
                ?? throw new LaunchException(
                    LaunchFailureKind.RuntimeMissing,
                    $"找不到 Proton「{protonRequest}」。请在启动设置里选择已安装的 Proton，或启用组件自动下载。");
        }

        var manifest = ToolManifest.Load(protonPath);
        var runtime = manifest.RequiredRuntime;
        if (runtime.Name != "host" && !string.IsNullOrEmpty(runtime.Variant))
        {
            if (provisioner is not null)
            {
                await provisioner
                    .EnsureRuntimeAsync(runtime.Variant, runtime.Name, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!IsRuntimeInstalled(runtime.Variant))
            {
                throw new LaunchException(
                    LaunchFailureKind.UmuRuntimeMissing,
                    $"Steam Runtime（{runtime.Variant}）尚未安装。请在启动设置里检查兼容层组件，或启用自动下载。");
            }
        }

        return (protonPath, manifest, runtime);
    }

    /// <summary>构建启动计划（不启动进程）：prefix + 环境 + 容器命令。umuId 覆盖 UMU_ID（空 = umu-{gameId}）。</summary>
    public UmuNativeLaunchPlan BuildPlan(
        string gameId,
        string installDir,
        string executablePath,
        string protonPath,
        ToolManifest manifest,
        SteamRuntimeInfo runtime,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        string? store = null,
        string? dataHomeOverride = null,
        string? umuId = null,
        string? resourceQualityTier = null,
        IReadOnlyList<string>? gameArguments = null)
    {
        EnsureLinux();

        var exe = Path.GetFullPath(Path.Combine(installDir, executablePath.Replace('\\', '/')));
        if (!File.Exists(exe))
        {
            throw new LaunchException(
                LaunchFailureKind.ExecutableMissing,
                $"游戏主程序不存在：{exe}。请检查可执行文件路径或安装目录。");
        }

        var home = dataHomeOverride ?? dataHome ?? AppPaths.DataHomeDirectory;
        var prefix = CompatTools.PrefixPathFor(gameId, dataHome: home);

        try
        {
            UmuPrefix.Setup(prefix, logger: logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UpdateException)
        {
            throw new LaunchException(
                LaunchFailureKind.PrefixCreateFailed,
                $"无法准备 Wine 前缀「{prefix}」：{ex.Message}",
                ex);
        }

        var installFullPath = Path.GetFullPath(installDir);
        var request = new UmuLaunchRequest(
            gameId, exe, installFullPath, protonPath, prefix, runtime, store, UmuId: umuId);
        var environment = UmuEnvironment.Build(request);
        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                // PROTONPATH 在配置里存的是发行版代号（DW-Proton 等），此处已被解析为绝对路径，不能回退覆盖
                if (key.Equals("PROTONPATH", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // PROTON_VERB 同为托管键：经 UmuEnvironment.Build 白名单校验/缺省回退，
                // 用户 env 反超会产出残缺 --verb 命令（F70）
                if (key.Equals("PROTON_VERB", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // UMU_ID/GAMEID 由 launch.umuId 显式覆盖时为权威值：配置 env 里的同名键（首运托管残留/
                // 存量迁移遗留）不得反超，否则 launch.umuId 覆盖静默失效（2026-09-28 托管语义实锤）；
                // umuId 未设置时仍允许 env 覆盖（存量手工配置的覆盖通道保持原样）
                if (!string.IsNullOrWhiteSpace(umuId)
                    && (key.Equals("UMU_ID", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("GAMEID", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // 与 GameLauncherService.Expand 一致：环境值支持 {exe}/{installDir}
                environment[key] = GameLauncherService.Expand(value, exe, installFullPath);
            }
        }

        // 游戏参数（2026-10-03 Steam 语义）：用户自定义 %command% 之后的 token（占位符展开同
        // 环境变量规则）在前，启动器生成的 -krqlv 档位参数在后，统一追加在 exe 之后
        var gameArgumentList = new List<string>();
        if (gameArguments is { Count: > 0 })
        {
            gameArgumentList.AddRange(gameArguments.Select(
                argument => GameLauncherService.Expand(argument, exe, installFullPath)));
        }

        if (!string.IsNullOrWhiteSpace(resourceQualityTier))
        {
            gameArgumentList.Add($"-krqlv={resourceQualityTier!.Trim()}");
        }

        var entry = BuildEntryCommand(
            manifest, runtime, environment["PROTON_VERB"], exe, dataHomeOverride ?? dataHome,
            gameArguments: gameArgumentList);
        var arguments = QuoteArgs(entry.Skip(1));

        // Debug 级全量启动面（2026-10-02 用户需求）：与 GameLauncherService 同型；Release 构建
        // 由组合根 SetMinimumLevel(Information) 过滤，不会输出
        logger?.LogDebug(
            "Native umu launch plan: {File} {Args} (workdir={WorkingDirectory}; env: {Environment})",
            entry[0], arguments, installFullPath, GameLauncherService.FormatEnvironment(environment));
        logger?.LogInformation(
            "Native umu plan: {File} {Args} (runtime={Runtime})",
            entry[0], arguments, runtime.Variant);

        return new UmuNativeLaunchPlan(
            entry[0],
            arguments,
            installFullPath,
            environment,
            protonPath,
            runtime,
            manifest);
    }

    /// <summary>解析组件 → 构建计划 → 即启即走启动，返回日志路径。umuId 覆盖 UMU_ID（空 = umu-{gameId}）；
    /// resourceQualityTier（鸣潮 hd/sd/uhd）与 gameArguments（自定义启动选项 %command% 之后的用户参数）
    /// 按序追加在游戏 exe 之后，空 = 不追加。</summary>
    public async Task<LaunchResult> LaunchAsync(
        string gameId,
        string installDir,
        string executablePath,
        string protonRequest,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        string? store = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        string? umuId = null,
        string? resourceQualityTier = null,
        IReadOnlyList<string>? gameArguments = null)
    {
        var (protonPath, manifest, runtime) = await ResolveComponentsAsync(
            protonRequest, progress, cancellationToken).ConfigureAwait(false);
        var plan = BuildPlan(
            gameId, installDir, executablePath, protonPath, manifest, runtime,
            extraEnvironment, store, umuId: umuId, resourceQualityTier: resourceQualityTier,
            gameArguments: gameArguments);

        var logDirectory = Path.Combine(AppPaths.DataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = FileUtilities.LaunchLogFilePath(logDirectory, gameId);

        var spec = new ProcessStartSpec(
            plan.FileName,
            plan.Arguments,
            plan.WorkingDirectory,
            plan.Environment,
            WaitForExit: false,
            OutputLogPath: logPath);

        try
        {
            var result = await processRunner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
            return new LaunchResult(result.ExitCode, logPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new LaunchException(
                LaunchFailureKind.StartFailed,
                $"无法启动原生 umu 进程「{plan.FileName}」：{ex.Message}",
                ex,
                logPath);
        }
    }

    /// <summary>
    /// 组装最终命令：
    /// {runtime}/_v2-entry-point --verb=… -- {proton}/proton {verb} {exe} [gameArguments…]
    /// host runtime（无容器）时直接调 proton；gameArguments（自定义启动选项 %command% 之后的
    /// 用户参数与启动器生成的 -krqlv 档位参数）追加在 argv 尾部（exe 之后）——null/空不追加。
    /// </summary>
    public static IReadOnlyList<string> BuildEntryCommand(
        ToolManifest manifest,
        SteamRuntimeInfo runtime,
        string verb,
        string exePath,
        string? dataHome = null,
        IReadOnlyList<string>? gameArguments = null)
    {
        var protonArgv = manifest.BuildEntryCommand(verb);
        string[] tail = gameArguments is { Count: > 0 } ? [exePath, .. gameArguments] : [exePath];
        if (runtime.Name == "host" || string.IsNullOrEmpty(runtime.Variant))
        {
            return [.. protonArgv, .. tail];
        }

        var runtimeRoot = UmuPaths.RuntimeDirectory(runtime.Variant, dataHome);
        var entry = Path.Combine(runtimeRoot, "_v2-entry-point");
        if (!File.Exists(entry) && File.Exists(Path.Combine(runtimeRoot, "umu")))
        {
            entry = Path.Combine(runtimeRoot, "umu");
        }

        return [entry, $"--verb={verb}", "--", .. protonArgv, .. tail];
    }

    private string? ResolveProtonLocally(string protonRequest)
    {
        if (string.IsNullOrWhiteSpace(protonRequest))
        {
            return null;
        }

        if (Path.IsPathRooted(protonRequest) && Directory.Exists(protonRequest))
        {
            return Path.GetFullPath(protonRequest);
        }

        var root = UmuPaths.SteamCompatRoot(dataHome);
        var candidate = Path.Combine(root, protonRequest);
        return Directory.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    private bool IsRuntimeInstalled(string variant)
    {
        var dir = UmuPaths.RuntimeDirectory(variant, dataHome);
        return Directory.Exists(dir) &&
               File.Exists(Path.Combine(dir, UmuPaths.InstallMarkerName));
    }

    private static void EnsureLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new LaunchException(
                LaunchFailureKind.Unknown,
                "原生 umu 启动仅支持 Linux。Windows 请使用直接运行。");
        }
    }

    /// <summary>把含空格的参数包上双引号后以空格拼接。</summary>
    private static string QuoteArgs(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}
