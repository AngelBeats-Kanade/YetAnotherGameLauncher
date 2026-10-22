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
/// <remarks>
/// <c>logDirectory</c> 是启动日志目录；覆盖它时应同时传 <c>appLogDirectory</c>（应用日志、
/// 超时提示用），否则提示可能指向组合根未写入的文件。应用日志目录不从启动日志目录反推。
/// </remarks>
public sealed class NativeUmuLauncher(
    IProcessRunner processRunner,
    IUmuComponentProvisioner? provisioner = null,
    ILogger? logger = null,
    string? dataHome = null,
    string? logDirectory = null,
    string? appLogDirectory = null)
{
    /// <summary>诱饵父进程桥准备器（终末地 1.6.3+ 启动校验；无状态，可安全共享）。</summary>
    private readonly DecoyParentBridge _decoyBridge = new(logger);

    /// <summary>启动日志目录（显式注入便于测试/自定义部署；缺省跟随 dataHome 下的 yagl/logs）。</summary>
    private readonly string _logDirectory = logDirectory
        ?? Path.Combine(dataHome ?? AppPaths.DataHomeDirectory, "yagl", "logs");

    /// <summary>应用日志目录（超时提示用；不能从启动日志目录反推）。</summary>
    private readonly string _appLogDirectory = appLogDirectory ?? AppPaths.LogDirectory;

    /// <summary>等待桥 READY/ERROR 的上限（测试注入；helper 写出首个状态后开始计时，生产 10 秒）。</summary>
    internal TimeSpan BridgeStatusTimeout { get; init; } = BridgeLaunchStatus.DefaultTimeout;

    /// <summary>等待 helper 写出首个状态的上限（测试注入；Linux 链含 Proton/umu 容器冷启动，生产 10 秒）。</summary>
    internal TimeSpan BridgeStartupTimeout { get; init; } = BridgeLaunchStatus.DefaultStartupTimeout;

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

    /// <summary>桥工作根：<c>{dataHome}/yagl/bridge/&lt;gameId&gt;</c>（dataHome 缺省取 AppPaths.DataHomeDirectory，Linux 尊重 XDG_DATA_HOME）。</summary>
    private string ResolveBridgeRoot(string gameId) =>
        Path.Combine(dataHome ?? AppPaths.DataHomeDirectory, "yagl", "bridge", gameId);

    /// <summary>构建启动计划（不启动进程）：prefix + 环境 + 容器命令。umuId 覆盖 UMU_ID（空 = umu-{gameId}）。
    /// decoyParent 非空时切换诱饵父进程桥：容器目标换成桥 helper，游戏命令行经 YAGL_BRIDGE_* 环境变量传递。</summary>
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
        IReadOnlyList<string>? gameArguments = null,
        DecoyParentPreparation? decoyParent = null,
        string? bridgeStatusPath = null)
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

        // Proton 日志目录与启动日志同源：feature 开关注入 PROTON_LOG=1 时，原生链用 launcher
        // 的日志目录覆盖调用方传入值（模板直启链在 BuildPlan 里也是同一目录）；未开开关时
        // 保留用户配置的 PROTON_LOG_DIR。
        if (environment.TryGetValue("PROTON_LOG", out var protonLog)
            && string.Equals(protonLog, "1", StringComparison.Ordinal))
        {
            environment["PROTON_LOG_DIR"] = _logDirectory;
        }

        // 游戏参数（2026-10-03 Steam 语义）：用户自定义 %command% 之后的 token 在前，启动器
        // 生成的 -krqlv 档位参数在后，统一追加在 exe 之后；占位符裸展开（F-H：参数走 argv，
        // 元素不得携带字面引号，空格由 argv 语义天然承载）
        var gameArgumentList = new List<string>();
        if (gameArguments is { Count: > 0 })
        {
            gameArgumentList.AddRange(gameArguments.Select(
                argument => GameLauncherService.ExpandRaw(argument, exe, installFullPath)));
        }

        if (!string.IsNullOrWhiteSpace(resourceQualityTier))
        {
            gameArgumentList.Add($"-krqlv={resourceQualityTier.Trim()}");
        }

        // 诱饵父进程桥（decoyParent 非空，2026-10-18）：容器目标换成桥 helper，游戏可执行文件与
        // 命令行经 YAGL_BRIDGE_* 传给 wine 侧 helper（它以挂起诱饵为父进程再拉起游戏）。
        // 写在用户环境合并之后：桥是基础设施键，不允许用户同名变量反超（与 PROTONPATH 同纪律）。
        var launchTarget = exe;
        IReadOnlyList<string>? launchArguments = gameArgumentList;
        if (decoyParent is not null)
        {
            var targetWindows = DecoyParentBridge.ToWindowsPath(exe);
            environment[DecoyParentBridge.DecoyVariable] = decoyParent.DecoyWinePath;
            environment[DecoyParentBridge.TargetVariable] = targetWindows;
            environment[DecoyParentBridge.CommandLineVariable] =
                ComposeWindowsCommandLine(targetWindows, gameArgumentList);
            environment[DecoyParentBridge.WorkingDirectoryVariable] =
                DecoyParentBridge.ToWindowsPath(installFullPath);
            if (!string.IsNullOrWhiteSpace(bridgeStatusPath))
            {
                environment[DecoyParentBridge.StatusVariable] =
                    DecoyParentBridge.ToWindowsPath(bridgeStatusPath);
            }

            launchTarget = decoyParent.HelperPath;
            launchArguments = null;
        }

        var entry = BuildEntryCommand(
            manifest, runtime, environment["PROTON_VERB"], launchTarget, dataHomeOverride ?? dataHome,
            gameArguments: launchArguments);
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
    /// 按序追加在游戏 exe 之后，空 = 不追加。decoyParent = true 时走诱饵父进程桥（prepare 失败静默回退直启）。</summary>
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
        IReadOnlyList<string>? gameArguments = null,
        bool decoyParent = false)
    {
        var (protonPath, manifest, runtime) = await ResolveComponentsAsync(
            protonRequest, progress, cancellationToken).ConfigureAwait(false);

        // 诱饵父进程桥（2026-10-18）：开启时先把诱饵与 helper 落盘。准备失败静默回退直启
        // （不阻断玩家；原因由 DecoyParentBridge 记日志）——游戏侧会照旧弹「请通过官方启动器」。
        DecoyParentPreparation? decoy = null;
        string? statusPath = null;
        if (decoyParent)
        {
            decoy = _decoyBridge.TryPrepare(installDir, ResolveBridgeRoot(gameId));
            if (decoy is not null)
            {
                try
                {
                    statusPath = BridgeLaunchStatus.CreateStatusPath(decoy.BridgeDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new LaunchException(
                        LaunchFailureKind.StartFailed,
                        $"无法创建诱饵父进程桥启动状态文件：{ex.Message}",
                        ex);
                }
            }
        }

        UmuNativeLaunchPlan plan;
        try
        {
            plan = BuildPlan(
                gameId, installDir, executablePath, protonPath, manifest, runtime,
                extraEnvironment, store, umuId: umuId, resourceQualityTier: resourceQualityTier,
                gameArguments: gameArguments, decoyParent: decoy, bridgeStatusPath: statusPath);
        }
        catch
        {
            // BuildPlan 失败（exe 缺失/配置错误）时 helper 尚未启动，清掉刚创建的占位文件
            BridgeLaunchStatus.TryDelete(statusPath);
            throw;
        }

        Directory.CreateDirectory(_logDirectory);
        var logPath = FileUtilities.LaunchLogFilePath(_logDirectory, gameId);

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
            if (decoy is not null && statusPath is not null)
            {
                var status = await BridgeLaunchStatus
                    .WaitAsync(
                        statusPath,
                        BridgeStatusTimeout,
                        cancellationToken,
                        startupTimeout: BridgeStartupTimeout,
                        logger: logger)
                    .ConfigureAwait(false);
                if (status.State == BridgeLaunchState.Failed)
                {
                    throw new LaunchException(
                        LaunchFailureKind.StartFailed,
                        $"{DecoyParentBridge.DescribeFailure(status.ExitCode)} {status.Message} " +
                        $"详见启动日志：{logPath}",
                        logPath: logPath);
                }

                if (status.State == BridgeLaunchState.TimedOut)
                {
                    var message = BridgeLaunchStatus.DescribeTimeout(
                        result: status,
                        readyTimeout: BridgeStatusTimeout,
                        startupTimeout: BridgeStartupTimeout,
                        logPath: logPath,
                        appLogDirectory: _appLogDirectory,
                        startupFailureReason: "Proton/umu 容器冷启动失败或桥 helper 未能运行");
                    throw new LaunchException(
                        LaunchFailureKind.StartFailed, message, logPath: logPath);
                }
            }

            return new LaunchResult(result.ExitCode, logPath);
        }
        catch (OperationCanceledException)
        {
            BridgeLaunchStatus.TryDelete(statusPath);
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            BridgeLaunchStatus.TryDelete(statusPath);
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

    /// <summary>
    /// 桥模式下的游戏命令行（YAGL_BRIDGE_CMD）：exe 恒加引号（Windows 路径可能含空格），
    /// 参数按 <see cref="QuoteArgs"/> 同规则按需加引号。这串最终成为游戏进程的 lpCommandLine。
    /// </summary>
    private static string ComposeWindowsCommandLine(string windowsExe, IReadOnlyList<string> gameArguments)
    {
        var command = $"\"{windowsExe}\"";
        foreach (var argument in gameArguments)
        {
            command += argument.Contains(' ') ? $" \"{argument}\"" : $" {argument}";
        }

        return command;
    }
}
