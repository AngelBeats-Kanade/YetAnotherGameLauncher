using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Dependencies;

/// <summary>依赖安装器（供游戏设置页依赖区消费；测试替身也实现此接口）。</summary>
public interface IDependencyInstaller
{
    /// <summary>内置依赖目录（清单已过校验）。</summary>
    IReadOnlyList<DependencyManifest> Dependencies { get; }

    /// <summary>读取指定目标处某依赖的安装状态（字体：完成标记；winetricks：标记+文件+覆盖+备份五态）。</summary>
    DependencyInstallState GetState(WinePrefixTarget target, DependencyManifest manifest);

    /// <summary>安装依赖到目标 prefix：下载→解压→字体落位→注册表导入→完成标记。</summary>
    Task InstallAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        IProgress<DependencyProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>卸载依赖并完整还原安装前状态（winetricks 来源）；字体来源为 no-op 成功。</summary>
    Task UninstallAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 依赖安装编排器。两种来源：
/// ① 字体（Bottles cjkfonts 语义）：前置校验 → 共享缓存下载（断点续传 + MD5/size 校验）→
/// 防御解压到暂存 → 字体拷进 windows/Fonts → 单次 <c>wine reg import</c>（UTF-16LE .reg）→
/// 原子写完成标记；任一阶段失败即中止且不写标记（幂等可重试），取消透传 OCE。
/// ② winetricks（原生 DLL）：win64 预检 → 解析 winetricks → wineserver -w 忙碌检查 →
/// 备份原始 DLL 形态（符号链接只记录 target，绝不写穿）与旧覆盖 → winetricks 安装 →
/// 逐条写 DllOverrides → DLL size/SHA-256 校验 → 最后写标记；失败/取消按备份完整回滚。
/// <see cref="UninstallAsync"/> 按备份还原（Proton 分支重建指向「当前」树的链接）。
/// 同一依赖的全局安装按 id 串行（设置页重建会换新 VM，IsBusy 门挡不住跨页并发）。
/// </summary>
public sealed class DependencyInstaller(
    IDownloader downloader,
    IProcessRunner processRunner,
    IReadOnlyList<DependencyManifest>? dependencies = null,
    ILogger? logger = null,
    string? cacheRoot = null,
    Func<string?, string?>? winetricksResolver = null) : IDependencyInstaller
{
    /// <summary>注册表导入的超时：reg import 只是写注册表文件，秒级动作；超时视为 wineserver 异常。</summary>
    private const int RegistryTimeoutMilliseconds = 120_000;

    /// <summary>winetricks 首次要下 Win7 SP1 x86+x64（约 1.5 GB），超时必须给足；测试钉住该常量。</summary>
    internal const int WinetricksTimeoutMilliseconds = 3_600_000;

    /// <summary>wineserver -w 等待现有 Wine 进程退出的超时（超时 = prefix 仍被占用）。</summary>
    internal const int PrefixBusyTimeoutMilliseconds = 10_000;

    /// <summary>状态查询（reg query）超时：GetState 是同步契约（UI 刷新直接调用），正常查询
    /// 亚秒级；超时压到 3 s，避免挂死的 wineserver 把页面冻结到 30 s。</summary>
    private const int StateQueryTimeoutMilliseconds = 3_000;

    /// <summary>wine 首次触碰 prefix 时可能弹 mono/gecko 安装框——注册表/winetricks 场景一律禁用。</summary>
    private const string SuppressMonoGeckoOverride = "mscoree,mshtml=";

    /// <summary>DllOverrides 键（wine reg 命令的 HKCU 形态）。</summary>
    private const string DllOverridesKey = @"HKCU\Software\Wine\DllOverrides";

    /// <summary>Wine 自己的父键：用于区分「DllOverrides 不存在」与「注册表读取本身坏了」。</summary>
    private const string WineRegistryKey = @"HKCU\Software\Wine";

    /// <summary>winetricks 账本文件名（prefix 根；内容是执行过的命令，含 verb 词元）。</summary>
    private const string WinetricksLedgerFileName = "winetricks.log";

    /// <summary>reg query 输出的值行：名称（可含单空格）+ 2 个以上空白 + REG_ 类型 + 数据。</summary>
    private static readonly Regex RegValueLine =
        new(@"^\s+(?<name>.+?)\s{2,}(?<type>REG_[A-Za-z0-9_]+)\s{2,}(?<data>.*)$",
            RegexOptions.Compiled);

    private readonly string _cacheRoot = cacheRoot ?? DependencyPaths.CacheRoot();

    /// <summary>winetricks 定位（参数 = Proton 根；返回可执行路径或 null）。生产默认走 <see cref="WinetricksResolver"/>。</summary>
    private readonly Func<string?, string?> _winetricksResolver =
        winetricksResolver ?? (protonRoot => WinetricksResolver.Resolve(protonRoot)?.ExecutablePath);

    /// <summary>按依赖 id 的安装互斥锁（静态：DI 单例也挡不住测试多实例与未来多入口）。</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallLocks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<DependencyManifest> Dependencies { get; } =
        dependencies ?? DependencyCatalog.LoadEmbedded();

    /// <inheritdoc />
    public DependencyInstallState GetState(WinePrefixTarget target, DependencyManifest manifest) =>
        manifest.WinetricksVerb is null
            ? GetMarkerState(target, manifest)
            : GetWinetricksState(target, manifest);

    /// <summary>字体来源状态：完成标记存在且内容为 id@version。</summary>
    private static DependencyInstallState GetMarkerState(WinePrefixTarget target, DependencyManifest manifest)
    {
        var markerPath = DependencyPaths.MarkerPath(target.StateDirectory, manifest.Id);
        string content;
        try
        {
            if (!File.Exists(markerPath))
            {
                return DependencyInstallState.NotInstalled;
            }

            content = File.ReadAllText(markerPath);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // 标记被并发删除/读失败（含 ACL 拒读）按"未安装"处理：重装是安全兜底（幂等）
            return DependencyInstallState.NotInstalled;
        }

        var expected = MarkerContent(manifest);
        return string.Equals(content.Trim(), expected, StringComparison.Ordinal)
            ? new DependencyInstallState(DependencyStateKind.Installed, manifest.Version)
            : DependencyInstallState.NotInstalled;
    }

    /// <summary>
    /// winetricks 来源五态：marker/备份清单组合决定可恢复性；已安装要求真实文件 + size/SHA-256 +
    /// 全部覆盖值 + 有效备份清单（与卸载共用同一清单校验，目录存在但清单损坏一律 BackupMissing）。
    /// </summary>
    private DependencyInstallState GetWinetricksState(
        WinePrefixTarget target, DependencyManifest manifest)
    {
        var markerPath = DependencyPaths.MarkerPath(target.StateDirectory, manifest.Id);
        var backupDirectory = DependencyPaths.BackupDirectory(target.StateDirectory, manifest.Id);
        var manifestPath = Path.Combine(backupDirectory, DependencyPaths.BackupManifestFileName);
        var pendingPath = Path.Combine(backupDirectory, DependencyPaths.BackupPendingFileName);
        var markerExists = File.Exists(markerPath);
        var backupDirectoryExists = Directory.Exists(backupDirectory);
        var backup = backupDirectoryExists
            ? TryLoadBackupManifest(backupDirectory, manifest)
            : null;

        if (!markerExists)
        {
            if (backup is not null)
            {
                return new DependencyInstallState(DependencyStateKind.NeedsRecovery, null);
            }

            if (!backupDirectoryExists)
            {
                return DependencyInstallState.NotInstalled;
            }

            // marker/manifest 都缺失可能是两种相反状态：
            // ① CreateBackupAsync 写 manifest 前被硬杀（`.incomplete` 哨兵仍在，prefix 未修改）；
            // ② 安装成功后 marker+manifest 丢失（哨兵已删或残留，prefix 已是原生 DLL/覆盖）。
            // 只有“哨兵存在 + DLL 不是原生 + 覆盖不是 App 期望值”才能确认是 ①；其余一律
            // BackupMissing，保留备份目录，防止重新备份把当前原生/残留态当成原始态（data loss）。
            return IsUncommittedBackupResidue(
                    target, manifest, markerExists, manifestPath, pendingPath)
                ? DependencyInstallState.NotInstalled
                : new DependencyInstallState(DependencyStateKind.BackupMissing, null);
        }

        if (backup is null)
        {
            return new DependencyInstallState(DependencyStateKind.BackupMissing, null);
        }

        string markerContent;
        try
        {
            markerContent = File.ReadAllText(markerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DependencyInstallState(DependencyStateKind.Damaged, null);
        }

        if (!string.Equals(markerContent.Trim(), MarkerContent(manifest), StringComparison.Ordinal)
            || !DllsMatch(target, manifest))
        {
            return new DependencyInstallState(DependencyStateKind.Damaged, null);
        }

        return OverridesMatch(target, manifest) switch
        {
            OverrideCheck.Mismatched => new DependencyInstallState(DependencyStateKind.Damaged, null),
            // 查询超时/启动失败 = 无法确认覆盖值，不是“值不匹配”。此时报 Damaged 会引导用户
            // 重装健康安装；保留 Installed 并用 VerificationUnavailable 让 UI 提示刷新重试。
            OverrideCheck.Unavailable => new DependencyInstallState(
                DependencyStateKind.Installed, manifest.Version, VerificationUnavailable: true),
            _ => new DependencyInstallState(DependencyStateKind.Installed, manifest.Version),
        };
    }

    /// <summary>已安装判据：每个目标是真实文件（非符号链接）且 size/SHA-256 精确匹配。</summary>
    private static bool DllsMatch(WinePrefixTarget target, DependencyManifest manifest)
    {
        foreach (var dll in manifest.Dlls ?? [])
        {
            try
            {
                var path = DllTargetPath(target, dll);
                if (new FileInfo(path).LinkTarget is not null
                    || !File.Exists(path)
                    || new FileInfo(path).Length != dll.SizeBytes
                    || !string.Equals(Hashing.Sha256Hex(path), dll.Sha256, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>覆盖值校验结果：匹配 / 明确不匹配 / 无法读取（超时、wine 启动失败）。</summary>
    private enum OverrideCheck
    {
        Matched,
        Mismatched,
        Unavailable,
    }

    /// <summary>
    /// 已安装判据：DllOverrides 每个值匹配。GetState 是同步契约（UI 刷新直接调用），
    /// 这里同步跑一次 reg query（读内存注册表，不受 user.reg 落盘延迟影响）。
    /// 查询成功但键/值缺失 = <see cref="OverrideCheck.Mismatched"/>（损坏）；非零退出用父键探针
    /// 区分「键不存在」（父键可读 = Mismatched）与「wine/注册表读取失败」（= Unavailable）；
    /// 超时或启动失败 = <see cref="OverrideCheck.Unavailable"/>（不能当成存在故障）。
    /// </summary>
    private OverrideCheck OverridesMatch(WinePrefixTarget target, DependencyManifest manifest)
    {
        if (manifest.DllOverrides is not { Count: > 0 } expectedValues)
        {
            return OverrideCheck.Matched;
        }

        try
        {
            var result = RunRegQuery(target, DllOverridesKey);
            if (!result.Succeeded)
            {
                // reg query 退出码 1 也可能只是键不存在；父键可读才说明注册表本身正常。
                return RunRegQuery(target, WineRegistryKey).Succeeded
                    ? OverrideCheck.Mismatched
                    : OverrideCheck.Unavailable;
            }

            var values = ParseRegValues(result.StandardOutput);
            foreach (var (key, expected) in expectedValues)
            {
                if (!values.TryGetValue(key, out var actual)
                    || !string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    return OverrideCheck.Mismatched;
                }
            }

            return OverrideCheck.Matched;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "读取 DllOverrides 失败，状态暂无法确认（不按损坏处理）");
            return OverrideCheck.Unavailable;
        }
    }

    /// <summary>
    /// 是否可安全判定为“备份阶段硬杀、prefix 未被修改”的预提交残留：marker/manifest 都缺失、
    /// `.incomplete` 哨兵存在、当前 DLL 不是清单里的原生文件、且覆盖值**明确不是** App 写入的
    /// 期望值（旧值可能是 builtin/缺失）。覆盖查询不可用或已经匹配原生值一律保守返回 false，
    /// 避免“DLL 已还原但覆盖仍是 App 值”的局部还原被当成原始态重新备份。
    /// </summary>
    private bool IsUncommittedBackupResidue(
        WinePrefixTarget target,
        DependencyManifest manifest,
        bool markerExists,
        string manifestPath,
        string pendingPath)
    {
        if (markerExists
            || File.Exists(manifestPath)
            || !File.Exists(pendingPath)
            || DllsMatch(target, manifest))
        {
            return false;
        }

        // manifest 没有覆盖项时无需比对；有覆盖项时必须**每个**期望键都不再等于 App 写入值。
        // 只要有一个键仍匹配（如只还原了 *msftedit、*second 仍是 native），就说明局部还原未完成；
        // 查询失败同样保守拒绝。
        return manifest.DllOverrides is not { Count: > 0 }
               || OverridesAllDifferFromExpected(target, manifest);
    }

    /// <summary>所有清单期望的覆盖键当前都不等于 App 写入值（键缺失/旧值均算），且查询可用。
    /// 用于预提交判定：任一键仍匹配 = 覆盖仍是安装器写入态，不能当原始态重新备份。</summary>
    private bool OverridesAllDifferFromExpected(WinePrefixTarget target, DependencyManifest manifest)
    {
        var expected = manifest.DllOverrides;
        if (expected is not { Count: > 0 })
        {
            return true;
        }

        try
        {
            var result = RunRegQuery(target, DllOverridesKey);
            Dictionary<string, string> values;
            if (result.Succeeded)
            {
                values = ParseRegValues(result.StandardOutput);
            }
            else if (RunRegQuery(target, WineRegistryKey).Succeeded)
            {
                // 父键可读 = 只是 DllOverrides 键不存在，视为全部无旧值
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                return false;
            }

            foreach (var (key, expectedValue) in expected)
            {
                if (values.TryGetValue(key, out var actual)
                    && string.Equals(actual, expectedValue, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "读取 DllOverrides 失败，无法确认预提交状态（保守保留备份）");
            return false;
        }
    }

    /// <summary>同步执行一次 reg query（GetState 的同步契约；超时抛 UpdateException 由调用方分类）。</summary>
    private ProcessResult RunRegQuery(WinePrefixTarget target, string key) =>
        processRunner.RunAsync(new ProcessStartSpec(
                target.WineExecutable,
                string.Empty,
                Environment: WineEnvironment(target),
                TimeoutMilliseconds: StateQueryTimeoutMilliseconds,
                ArgumentList: ["reg", "query", key]))
            .ConfigureAwait(false).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task InstallAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        IProgress<DependencyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // UI 线程让位（同 IncrementalUpdateService.ApplyAsync 先例）：VM 命令从 UI 线程发起，
        // 无竞争 SemaphoreSlim.WaitAsync 会同步返回已完成 Task，若入口不显式让位到线程池，
        // 前缀预检、GetState（含双 DLL SHA-256 与同步阻塞的 wine reg query）、RepairBuiltinLinks
        // 等耗时操作都会跑在 UI 线程，导致富文本控件安装时进度条卡顿。
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        // 前置校验先行：wine/prefix 缺失时绝不发起下载/安装（字体包 90MB，winetricks 首次 1.5GB）
        ValidateWineAndPrefix(target);
        var protonRoot = TryDeriveProtonRoot(target.WineExecutable);

        if (manifest.WinetricksVerb is not null)
        {
            // 架构预检零修改；winetricks 缺失也在任何写操作之前报错
            ValidateWin64Prefix(target);
            var winetricks = _winetricksResolver(protonRoot);
            if (winetricks is null)
            {
                throw new DependencyException(
                    DependencyFailureKind.WinetricksMissing,
                    "未找到 winetricks：Proton 自带与 PATH 都没有。请安装 winetricks、换用自带 winetricks 的 "
                    + "Proton，或按 README 依赖说明手动安装原生 msftedit。");
            }

            var nativeLock = InstallLocks.GetOrAdd(manifest.Id, _ => new SemaphoreSlim(1, 1));
            await nativeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (GetState(target, manifest) is { Installed: true })
                {
                    logger?.LogInformation(
                        "Dependency {Id}@{Version} already installed, skipping",
                        manifest.Id, manifest.Version);
                    return;
                }

                await InstallWinetricksAsync(
                    target, manifest, protonRoot, winetricks, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                nativeLock.Release();
            }

            return;
        }

        // Proton 升级删除旧目录会把 prefix 的 builtin 链接全部悬空（wine 任何 PE 进程起不来，
        // 退出码 53 / c0000135）：注册表阶段前先把存量损坏迁到当前 Proton 树；迁不干净在
        // 下载 90MB 级字体包之前就失败。系统 wine 模式（路径无 files/bin/wine 形态）无此损坏
        // 形态，跳过
        RepairBuiltinLinks(target, protonRoot);

        var installLock = InstallLocks.GetOrAdd(manifest.Id, _ => new SemaphoreSlim(1, 1));
        await installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (GetState(target, manifest) is { Installed: true })
            {
                logger?.LogInformation(
                    "Dependency {Id}@{Version} already installed, skipping", manifest.Id, manifest.Version);
                return;
            }

            await InstallCoreAsync(target, manifest, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            installLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task UninstallAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        CancellationToken cancellationToken = default)
    {
        // 字体来源没有可还原的接管物：no-op 成功
        if (manifest.WinetricksVerb is null)
        {
            return;
        }

        // UI 线程让位（同 InstallAsync）：避免还原/重装前段的备份读取、链接修复与子进程启动阻塞 UI 线程
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        ValidateWineAndPrefix(target);

        // 与 InstallAsync 共用同一 id 锁：安装取消后的回滚在后台用 CancellationToken.None 继续，
        // 新页面的还原/修复不得与它并发改同一批 DLL、覆盖和备份目录。
        var uninstallLock = InstallLocks.GetOrAdd(manifest.Id, _ => new SemaphoreSlim(1, 1));
        await uninstallLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var markerPath = DependencyPaths.MarkerPath(target.StateDirectory, manifest.Id);
            var backupDirectory = DependencyPaths.BackupDirectory(target.StateDirectory, manifest.Id);
            if (!File.Exists(markerPath))
            {
                throw new DependencyException(
                    DependencyFailureKind.BackupMissing,
                    $"安装标记缺失（{markerPath}），无法确认安装状态；若备份目录含 "
                    + $"{DependencyPaths.BackupPendingFileName} 且 prefix 仍是原状，删除 {backupDirectory} "
                    + "后重试；否则保留备份目录，按 README/PITFALLS 手动恢复。");
            }

            var backup = TryLoadBackupManifest(backupDirectory, manifest);
            if (backup is null)
            {
                throw new DependencyException(
                    DependencyFailureKind.BackupMissing,
                    $"备份缺失或清单损坏（{backupDirectory}），无法自动还原；请按 README 依赖说明手动恢复。");
            }

            var protonRoot = TryDeriveProtonRoot(target.WineExecutable);
            await EnsurePrefixNotBusyAsync(target, protonRoot, cancellationToken).ConfigureAwait(false);
            // Proton 升级删除旧树会把 prefix 的 builtin 链接全部悬空：不先修复，还原覆盖的
            // wine reg 调用会起不来（同安装预检）
            RepairBuiltinLinks(target, protonRoot);

            // 通过预检后即进入不可取消的还原：半还原状态比让用户重试更危险
            try
            {
                await RestoreCoreAsync(
                    target, manifest, backup, protonRoot, CancellationToken.None).ConfigureAwait(false);
            }
            catch (DependencyException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new DependencyException(
                    DependencyFailureKind.RestoreFailed, $"还原失败：{ex.Message}", ex);
            }

            TryCleanWinetricksLedger(target, manifest);

            // 先删备份目录，成功后再删 marker；删除失败只告警（已还原的 DLL/覆盖不受影响）
            try
            {
                if (Directory.Exists(backupDirectory))
                {
                    Directory.Delete(backupDirectory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(
                    ex, "依赖备份目录删除失败，保留 marker 供手动清理：{Backup}", backupDirectory);
                return;
            }

            try
            {
                File.Delete(markerPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "依赖安装标记删除失败（可手动删除）：{Marker}", markerPath);
            }
        }
        finally
        {
            uninstallLock.Release();
        }
    }

    /// <summary>安装主体（字体来源；调用方已持依赖锁并做过已装短路）。</summary>
    private async Task InstallCoreAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        IProgress<DependencyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var archivePath = await EnsureArchiveAsync(manifest, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new DependencyProgress(DependencyPhase.Extracting, null));
        var staging = DependencyPaths.StagingDirectory(_cacheRoot, manifest.Id);
        // 预清上一轮硬杀（进程终止，finally 无法执行）可能留下的旧暂存：陈旧字体文件会被
        // ExtractFonts 的存在性校验误当成本包产物
        FileUtilities.TryDeleteDirectory(staging, logger);
        List<string> fontFiles;
        try
        {
            // 暂存清理覆盖解压与落位全程：解压产物可达百 MB 级，成败都用后即清
            fontFiles = ExtractFonts(manifest, archivePath, staging);

            progress?.Report(new DependencyProgress(DependencyPhase.Copying, null));
            CopyFonts(target, manifest, fontFiles);

            progress?.Report(new DependencyProgress(DependencyPhase.Registering, null));
            await ImportRegistryAsync(target, manifest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FileUtilities.TryDeleteDirectory(staging, logger);
        }

        await WriteMarkerAsync(target, manifest).ConfigureAwait(false);
        progress?.Report(new DependencyProgress(DependencyPhase.Done, null));
        logger?.LogInformation(
            "Dependency {Id}@{Version} installed into {Prefix}", manifest.Id, manifest.Version, target.WinePrefixDirectory);
    }

    /// <summary>
    /// winetricks 安装主体：忙碌检查（任何写操作前）→ builtin 链接修复 → 崩溃恢复/备份保护 →
    /// 备份 → winetricks → 覆盖 → DLL 校验 → 最后写 marker；失败/取消按备份回滚。
    /// </summary>
    private async Task InstallWinetricksAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        string? protonRoot,
        string winetricksPath,
        IProgress<DependencyProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 忙碌检查（最长 10 s）与备份阶段也要让 UI 显示不定进度，不能看起来像卡死
        progress?.Report(new DependencyProgress(DependencyPhase.Installing, null));
        await EnsurePrefixNotBusyAsync(target, protonRoot, cancellationToken).ConfigureAwait(false);
        // 修复会写 prefix，必须在忙碌检查之后；wine 调用（reg/winetricks）依赖链接可用
        RepairBuiltinLinks(target, protonRoot);

        var backupDirectory = DependencyPaths.BackupDirectory(target.StateDirectory, manifest.Id);
        var markerPath = DependencyPaths.MarkerPath(target.StateDirectory, manifest.Id);
        var manifestPath = Path.Combine(backupDirectory, DependencyPaths.BackupManifestFileName);
        var pendingPath = Path.Combine(backupDirectory, DependencyPaths.BackupPendingFileName);
        var backupDirectoryExists = Directory.Exists(backupDirectory);
        var markerExists = File.Exists(markerPath);

        if (backupDirectoryExists)
        {
            var existing = TryLoadBackupManifest(backupDirectory, manifest);
            if (existing is null)
            {
                if (IsUncommittedBackupResidue(
                        target, manifest, markerExists, manifestPath, pendingPath))
                {
                    // 只有哨兵仍在、DLL/覆盖都还是原状才能确认是“备份阶段硬杀、prefix 未修改”：
                    // 清理半成品后按当前原始态重新备份。无哨兵/已是原生/覆盖仍是 App 值时保留
                    // 备份目录，拒绝重新备份。
                    logger?.LogInformation(
                        "清理未提交的依赖备份残留：{Backup}", backupDirectory);
                    FileUtilities.TryDeleteDirectory(backupDirectory, logger);
                }
                else
                {
                    throw new DependencyException(
                        DependencyFailureKind.BackupMissing,
                        $"备份目录存在但清单不可用（{backupDirectory}），无法确认是否已安装；"
                        + "保留备份目录，请按 README/PITFALLS 手动恢复。");
                }
            }
            else if (markerExists)
            {
                // 有备份 + marker：损坏态/异常重入。绝不能把当前真实 DLL 当成原始文件重新备份
                throw new DependencyException(
                    DependencyFailureKind.RecoveryFailed,
                    "检测到安装标记与备份同时存在：请先点「还原」或「修复」，不会重新备份当前文件。");
            }
            else
            {
                progress?.Report(new DependencyProgress(DependencyPhase.Installing, null));
                try
                {
                    await RestoreCoreAsync(
                        target, manifest, existing, protonRoot, CancellationToken.None).ConfigureAwait(false);
                    if (Directory.Exists(backupDirectory))
                    {
                        Directory.Delete(backupDirectory, recursive: true);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new DependencyException(
                        DependencyFailureKind.RecoveryFailed,
                        $"上次安装未完成，自动恢复失败：{ex.Message}", ex);
                }
            }
        }
        else if (markerExists)
        {
            throw new DependencyException(
                DependencyFailureKind.BackupMissing,
                "安装标记存在但备份缺失：不会重新备份或安装；请按 README 依赖说明手动恢复。");
        }

        var backup = await CreateBackupAsync(target, manifest, backupDirectory, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            progress?.Report(new DependencyProgress(DependencyPhase.Installing, null));
            await RunWinetricksAsync(target, manifest, protonRoot, winetricksPath, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new DependencyProgress(DependencyPhase.Registering, null));
            await WriteOverridesAsync(target, manifest, cancellationToken).ConfigureAwait(false);
            VerifyDlls(target, manifest);
            await WriteMarkerAsync(target, manifest).ConfigureAwait(false);

            progress?.Report(new DependencyProgress(DependencyPhase.Done, null));
            logger?.LogInformation(
                "Native dependency {Id}@{Version} installed via winetricks into {Prefix}",
                manifest.Id, manifest.Version, target.WinePrefixDirectory);
        }
        catch (OperationCanceledException)
        {
            var rollback = await TryRollbackAsync(target, manifest, backup, backupDirectory)
                .ConfigureAwait(false);
            if (rollback is not null)
            {
                throw new DependencyException(
                    DependencyFailureKind.RollbackFailed,
                    $"安装被取消，且回滚未完成：{rollback.Message}；备份保留在 {backupDirectory}",
                    rollback);
            }

            throw;
        }
        catch (Exception ex)
        {
            var rollback = await TryRollbackAsync(target, manifest, backup, backupDirectory)
                .ConfigureAwait(false);
            if (rollback is not null)
            {
                throw new DependencyException(
                    DependencyFailureKind.RollbackFailed,
                    $"安装失败：{ex.Message}；回滚失败：{rollback.Message}；备份保留在 {backupDirectory}",
                    ex);
            }

            throw;
        }
    }

    /// <summary>wine 可用 + prefix 已初始化（drive_c 存在）的前置校验。</summary>
    private static void ValidateWineAndPrefix(WinePrefixTarget target)
    {
        if (!FileUtilities.IsExecutableFile(target.WineExecutable))
        {
            throw new DependencyException(
                DependencyFailureKind.WineMissing,
                $"wine 不可用：{target.WineExecutable}");
        }

        if (!Directory.Exists(Path.Combine(target.WinePrefixDirectory, "drive_c")))
        {
            throw new DependencyException(
                DependencyFailureKind.PrefixMissing,
                $"Wine prefix 未初始化（缺 drive_c）：{target.WinePrefixDirectory}。请先启动一次游戏。");
        }
    }

    /// <summary>
    /// win64 架构预检（零修改）：system.reg 首部 #arch=win64 且 syswow64 存在；
    /// win32 → PrefixUnsupported，损坏/缺 #arch → PrefixUnhealthy，文件缺失 → PrefixMissing。
    /// </summary>
    private static void ValidateWin64Prefix(WinePrefixTarget target)
    {
        var systemRegPath = Path.Combine(target.WinePrefixDirectory, "system.reg");
        string? arch = null;
        try
        {
            if (!File.Exists(systemRegPath))
            {
                throw new DependencyException(
                    DependencyFailureKind.PrefixMissing,
                    $"Wine prefix 缺少 system.reg：{systemRegPath}。请先启动一次游戏。");
            }

            using var reader = new StreamReader(systemRegPath);
            for (var lineNumber = 0; lineNumber < 10 && reader.ReadLine() is { } line; lineNumber++)
            {
                if (line.StartsWith("#arch=", StringComparison.Ordinal))
                {
                    arch = line["#arch=".Length..].Trim();
                    break;
                }
            }
        }
        catch (DependencyException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DependencyException(
                DependencyFailureKind.PrefixUnhealthy,
                $"无法读取 prefix 架构信息：{systemRegPath}：{ex.Message}",
                ex);
        }

        if (arch is null)
        {
            throw new DependencyException(
                DependencyFailureKind.PrefixUnhealthy,
                $"prefix 的 system.reg 缺少 #arch 标记（可能已损坏）：{systemRegPath}");
        }

        if (!string.Equals(arch, "win64", StringComparison.Ordinal))
        {
            if (string.Equals(arch, "win32", StringComparison.Ordinal))
            {
                throw new DependencyException(
                    DependencyFailureKind.PrefixUnsupported,
                    $"该依赖仅支持 win64 prefix（system.reg #arch=win32）：{target.WinePrefixDirectory}");
            }

            throw new DependencyException(
                DependencyFailureKind.PrefixUnhealthy,
                $"无法识别的 prefix 架构 #arch={arch}：{systemRegPath}");
        }

        if (!Directory.Exists(Path.Combine(target.WinePrefixDirectory, "drive_c", "windows", "syswow64")))
        {
            throw new DependencyException(
                DependencyFailureKind.PrefixUnhealthy,
                $"win64 prefix 缺少 drive_c/windows/syswow64（可能已损坏）：{target.WinePrefixDirectory}");
        }
    }

    /// <summary>Proton 型 prefix 的 builtin 链接预检修复；迁不干净按 PrefixUnhealthy 失败。</summary>
    private void RepairBuiltinLinks(WinePrefixTarget target, string? protonRoot)
    {
        if (protonRoot is null)
        {
            return;
        }

        var repair = WinePrefixBuiltinRepair.RepairDangling(target.WinePrefixDirectory, protonRoot);
        if (repair.Unrepairable > 0)
        {
            throw new DependencyException(
                DependencyFailureKind.PrefixUnhealthy,
                $"Wine prefix 内置组件链接失效且自动修复未完成（残留 {repair.Unrepairable} 个）："
                + $"{target.WinePrefixDirectory}（当前 Proton：{protonRoot}）");
        }

        if (repair.Repaired > 0)
        {
            logger?.LogInformation(
                "Relinked {Count} dangling builtin links in {Prefix} to {Proton}",
                repair.Repaired, target.WinePrefixDirectory, protonRoot);
        }
    }

    /// <summary>
    /// PrefixBusy：wineserver -w 等待现有 Wine 进程退出（超时 = 游戏/其它 Wine 程序在跑）。
    /// 在任何写操作之前调用；wineserver 不存在时仅告警并放行（无法探测，不阻塞系统 wine 用户）。
    /// </summary>
    private async Task EnsurePrefixNotBusyAsync(
        WinePrefixTarget target, string? protonRoot, CancellationToken cancellationToken)
    {
        var wineserver = ResolveWineserver(target, protonRoot);
        if (wineserver is null)
        {
            logger?.LogWarning(
                "wineserver 未找到，跳过 prefix 占用检查：{Prefix}", target.WinePrefixDirectory);
            return;
        }

        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                new ProcessStartSpec(
                    wineserver,
                    "-w",
                    Environment: WineEnvironment(target),
                    TimeoutMilliseconds: PrefixBusyTimeoutMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DependencyException(
                DependencyFailureKind.PrefixBusy,
                $"Wine prefix 仍被占用（wineserver -w 未在 {PrefixBusyTimeoutMilliseconds / 1000} 秒内完成）："
                + $"请先退出游戏或其它 Wine 程序。{ex.Message}",
                ex);
        }

        if (!result.Succeeded)
        {
            throw new DependencyException(
                DependencyFailureKind.PrefixBusy,
                $"Wine prefix 仍被占用（wineserver -w 退出码 {result.ExitCode}）：请先退出游戏后重试。");
        }
    }

    /// <summary>wineserver 定位：Proton 树 → 与 wine 同目录 → PATH；都没有返回 null。</summary>
    private static string? ResolveWineserver(WinePrefixTarget target, string? protonRoot)
    {
        if (protonRoot is not null)
        {
            var protonWineserver = Path.Combine(protonRoot, "files", "bin", "wineserver");
            if (FileUtilities.IsExecutableFile(protonWineserver))
            {
                return protonWineserver;
            }
        }

        var wineDirectory = Path.GetDirectoryName(Path.GetFullPath(target.WineExecutable));
        if (wineDirectory is not null)
        {
            var sibling = Path.Combine(wineDirectory, "wineserver");
            if (FileUtilities.IsExecutableFile(sibling))
            {
                return sibling;
            }
        }

        return CompatTools.FindOnPath("wineserver");
    }

    /// <summary>基础 Wine 环境：WINEPREFIX + 防 mono/gecko 弹窗覆盖（每次 wine 调用一致）。</summary>
    private static Dictionary<string, string> WineEnvironment(WinePrefixTarget target) =>
        new(StringComparer.Ordinal)
        {
            ["WINEPREFIX"] = target.WinePrefixDirectory,
            ["WINEDLLOVERRIDES"] = SuppressMonoGeckoOverride,
        };

    /// <summary>执行 winetricks（argv 形态）：--unattended --force &lt;verb&gt;，显式 WINEPREFIX/WINE/WINESERVER。</summary>
    private async Task RunWinetricksAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        string? protonRoot,
        string winetricksPath,
        CancellationToken cancellationToken)
    {
        var environment = WineEnvironment(target);
        environment["WINE"] = target.WineExecutable;
        if (ResolveWineserver(target, protonRoot) is { } wineserver)
        {
            environment["WINESERVER"] = wineserver;
        }

        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                new ProcessStartSpec(
                    winetricksPath,
                    string.Empty,
                    Environment: environment,
                    TimeoutMilliseconds: WinetricksTimeoutMilliseconds,
                    ArgumentList: ["--unattended", "--force", manifest.WinetricksVerb!]),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DependencyException(
                DependencyFailureKind.WinetricksFailed,
                $"winetricks 启动失败：{winetricksPath}：{ex.Message}", ex);
        }

        if (result.Succeeded)
        {
            logger?.LogInformation(
                "winetricks {Verb} succeeded for {Prefix}", manifest.WinetricksVerb, target.WinePrefixDirectory);
            return;
        }

        var logPath = await WriteWinetricksLogAsync(target, manifest, result).ConfigureAwait(false);
        throw new DependencyException(
            DependencyFailureKind.WinetricksFailed,
            $"winetricks {manifest.WinetricksVerb} 退出码 {result.ExitCode}："
            + $"{Tail(result.StandardError, 2000)}；完整日志：{logPath}");
    }

    /// <summary>winetricks 失败时把完整输出落盘（失败日志留证），返回日志路径（写失败也返回路径）。</summary>
    private async Task<string> WriteWinetricksLogAsync(
        WinePrefixTarget target, DependencyManifest manifest, ProcessResult result)
    {
        var logPath = Path.Combine(target.StateDirectory, $"{manifest.Id}-winetricks.log");
        try
        {
            Directory.CreateDirectory(target.StateDirectory);
            await File.WriteAllTextAsync(
                logPath,
                $"""
                # YAGL winetricks log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}
                # exit code: {result.ExitCode}
                {result.StandardOutput}
                {result.StandardError}
                """).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "winetricks 日志写入失败：{Path}", logPath);
        }

        return logPath;
    }

    /// <summary>取字符串尾部（错误消息只带最后一段，完整内容在日志文件）。</summary>
    private static string Tail(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[^maxLength..];

    /// <summary>逐条写入 DllOverrides（持久化；只用 WINEDLLOVERRIDES 环境变量会随启动器退出失效）。</summary>
    private async Task WriteOverridesAsync(
        WinePrefixTarget target, DependencyManifest manifest, CancellationToken cancellationToken)
    {
        foreach (var (key, value) in manifest.DllOverrides ?? new Dictionary<string, string>())
        {
            ProcessResult result;
            try
            {
                result = await RunWineRegAsync(
                    target,
                    ["reg", "add", DllOverridesKey, "/v", key, "/t", "REG_SZ", "/d", value, "/f"],
                    RegistryTimeoutMilliseconds,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DependencyException(
                    DependencyFailureKind.OverrideFailed,
                    $"写入 DllOverrides {key} 启动失败：{ex.Message}", ex);
            }

            if (!result.Succeeded)
            {
                throw new DependencyException(
                    DependencyFailureKind.OverrideFailed,
                    $"写入 DllOverrides {key} 失败（退出码 {result.ExitCode}）：{result.StandardError}");
            }
        }
    }

    /// <summary>winetricks 返回后校验每个目标为真实文件（非符号链接）且 size/SHA-256 精确匹配。</summary>
    private static void VerifyDlls(WinePrefixTarget target, DependencyManifest manifest)
    {
        foreach (var dll in manifest.Dlls ?? [])
        {
            var path = DllTargetPath(target, dll);
            try
            {
                if (new FileInfo(path).LinkTarget is not null)
                {
                    throw new DependencyException(
                        DependencyFailureKind.DllVerifyFailed,
                        $"winetricks 后目标仍是符号链接（可能写穿 Proton）：{path}");
                }

                if (!File.Exists(path))
                {
                    throw new DependencyException(
                        DependencyFailureKind.DllVerifyFailed, $"winetricks 后目标 DLL 不存在：{path}");
                }

                var length = new FileInfo(path).Length;
                if (length != dll.SizeBytes)
                {
                    throw new DependencyException(
                        DependencyFailureKind.DllVerifyFailed,
                        $"目标 DLL 尺寸不符：{path}（期望 {dll.SizeBytes}，实际 {length}）");
                }

                var sha256 = Hashing.Sha256Hex(path);
                if (!string.Equals(sha256, dll.Sha256, StringComparison.Ordinal))
                {
                    throw new DependencyException(
                        DependencyFailureKind.DllVerifyFailed,
                        $"目标 DLL SHA-256 不符：{path}（期望 {dll.Sha256}，实际 {sha256}）");
                }
            }
            catch (DependencyException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DependencyException(
                    DependencyFailureKind.DllVerifyFailed, $"校验目标 DLL 失败：{path}：{ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// 备份（仅在无备份时执行）：每个目标记录 Symlink（target，绝不跟随）/ File（复制 + SHA-256）/
    /// Missing；每个覆盖键记录旧值（无键/无值 = null）并导出整键 .reg。任何失败 → BackupFailed 且清理半成品。
    /// </summary>
    private async Task<DependencyBackupManifest> CreateBackupAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(backupDirectory);
            // 预提交哨兵：先落盘再复制/导出，manifest 原子写成功后才删除。GetState 用它区分
            // “备份阶段硬杀（prefix 未修改）”与“安装成功后 marker/manifest 丢失（已是原生 DLL）”。
            var pendingPath = Path.Combine(backupDirectory, DependencyPaths.BackupPendingFileName);
            await FileUtilities.WriteAtomicAsync(
                pendingPath, $"{manifest.Id}@{manifest.Version}", cancellationToken).ConfigureAwait(false);
            List<DependencyDllBackup> dlls = [];
            foreach (var dll in manifest.Dlls ?? [])
            {
                var path = DllTargetPath(target, dll);
                var linkTarget = GetLinkTarget(path);
                if (linkTarget is not null)
                {
                    dlls.Add(new DependencyDllBackup(
                        dll.Arch, dll.FileName, DependencyBackupKind.Symlink, linkTarget, null, null));
                }
                else if (File.Exists(path))
                {
                    var backupFileName = $"{dll.Arch}-{dll.FileName}";
                    var backupPath = Path.Combine(backupDirectory, backupFileName);
                    File.Copy(path, backupPath, overwrite: true);
                    dlls.Add(new DependencyDllBackup(
                        dll.Arch, dll.FileName, DependencyBackupKind.File, null, backupFileName,
                        Hashing.Sha256Hex(path)));
                }
                else
                {
                    dlls.Add(new DependencyDllBackup(
                        dll.Arch, dll.FileName, DependencyBackupKind.Missing, null, null, null));
                }
            }

            var overrides = await BackupOverridesAsync(
                target, manifest, backupDirectory, cancellationToken).ConfigureAwait(false);
            var backup = new DependencyBackupManifest(manifest.Id, manifest.Version, dlls, overrides);
            await FileUtilities.WriteAtomicAsync(
                Path.Combine(backupDirectory, DependencyPaths.BackupManifestFileName),
                JsonSerializer.Serialize(backup, Json.Default)).ConfigureAwait(false);
            // manifest 已原子落盘：哨兵删除失败不影响状态判定（TryLoad 优先），仅留一个无害文件。
            try
            {
                File.Delete(pendingPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "清理备份哨兵失败（manifest 已提交，不影响状态）：{Path}", pendingPath);
            }

            logger?.LogInformation(
                "Backed up native dependency {Id} into {Backup}", manifest.Id, backupDirectory);
            return backup;
        }
        catch (OperationCanceledException)
        {
            FileUtilities.TryDeleteDirectory(backupDirectory, logger);
            throw;
        }
        catch (DependencyException)
        {
            FileUtilities.TryDeleteDirectory(backupDirectory, logger);
            throw;
        }
        catch (Exception ex)
        {
            FileUtilities.TryDeleteDirectory(backupDirectory, logger);
            throw new DependencyException(
                DependencyFailureKind.BackupFailed, $"备份现有 DLL/注册表失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 记录每个覆盖键旧值 + 导出整键：reg query 整键一次（退出码 1 时用父键探针区分「键不存在」与
    /// 「注册表读取坏了」）；键存在才 export（不存在 = 全部无旧值，不算失败）。
    /// </summary>
    private async Task<List<DependencyOverrideBackup>> BackupOverridesAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string>? values = null;
        var query = await RunWineRegAsync(
            target, ["reg", "query", DllOverridesKey], RegistryTimeoutMilliseconds, cancellationToken)
            .ConfigureAwait(false);
        if (query.Succeeded)
        {
            values = ParseRegValues(query.StandardOutput);
        }
        else
        {
            // 退出码 1 也可能来自「键不存在」；父键可读才说明只是键不存在
            var probe = await RunWineRegAsync(
                target, ["reg", "query", WineRegistryKey], RegistryTimeoutMilliseconds, cancellationToken)
                .ConfigureAwait(false);
            if (!probe.Succeeded)
            {
                throw new DependencyException(
                    DependencyFailureKind.BackupFailed,
                    $"读取 Wine 注册表失败（reg query 退出码 {query.ExitCode}）：{query.StandardError}");
            }
        }

        if (values is not null)
        {
            var exportPath = Path.Combine(backupDirectory, "dlloverrides.reg");
            var export = await RunWineRegAsync(
                target,
                ["reg", "export", DllOverridesKey, exportPath, "/y"],
                RegistryTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            if (!export.Succeeded)
            {
                throw new DependencyException(
                    DependencyFailureKind.BackupFailed,
                    $"导出 DllOverrides 失败（退出码 {export.ExitCode}）：{export.StandardError}");
            }
        }

        List<DependencyOverrideBackup> entries = [];
        foreach (var key in manifest.DllOverrides?.Keys ?? [])
        {
            entries.Add(new DependencyOverrideBackup(
                key, values is not null && values.TryGetValue(key, out var oldValue) ? oldValue : null));
        }

        return entries;
    }

    /// <summary>解析 wine reg query 输出里的值行（名称/REG_ 类型/数据，名称可含单个空格）。
    /// 注册表值名大小写不敏感（`*MSFTEDIT` 与 `*msftedit` 是同一个值），值数据仍按原样返回。</summary>
    private static Dictionary<string, string> ParseRegValues(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var match = RegValueLine.Match(raw.TrimEnd('\r'));
            if (match.Success)
            {
                values[match.Groups["name"].Value] = match.Groups["data"].Value;
            }
        }

        return values;
    }

    /// <summary>读取符号链接 target；非链接返回 null。IO 异常上抛（备份阶段按 BackupFailed 处理）。</summary>
    private static string? GetLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"无法读取链接信息：{path}", ex);
        }
    }

    /// <summary>回滚（CancellationToken.None）：按备份还原 DLL/覆盖，删 marker，全部成功才删备份目录。</summary>
    private async Task<Exception?> TryRollbackAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        DependencyBackupManifest backup,
        string backupDirectory)
    {
        try
        {
            await RestoreDllsAsync(
                target, manifest, backup, currentProtonRoot: null, CancellationToken.None).ConfigureAwait(false);
            await RestoreOverridesAsync(target, backup, CancellationToken.None).ConfigureAwait(false);

            File.Delete(DependencyPaths.MarkerPath(target.StateDirectory, manifest.Id));
            if (Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, recursive: true);
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>还原 DLL 与覆盖（卸载/崩溃恢复/回滚共用）；currentProtonRoot 非空时按当前树重建链接。</summary>
    private async Task RestoreCoreAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        DependencyBackupManifest backup,
        string? currentProtonRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            await RestoreDllsAsync(target, manifest, backup, currentProtonRoot, cancellationToken)
                .ConfigureAwait(false);
            await RestoreOverridesAsync(target, backup, cancellationToken).ConfigureAwait(false);
        }
        catch (DependencyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DependencyException(
                DependencyFailureKind.RestoreFailed, $"还原失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 逐个还原 DLL：Symlink → 校验目标存在后重建链接（Proton 分支指向当前树，绝不创建悬空链接）；
    /// File → 备份复制回去并校验 SHA-256；Missing → 删除。
    /// </summary>
    private async Task RestoreDllsAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        DependencyBackupManifest backup,
        string? currentProtonRoot,
        CancellationToken cancellationToken)
    {
        foreach (var dll in manifest.Dlls ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = backup.Dlls.SingleOrDefault(
                d => string.Equals(d.Arch, dll.Arch, StringComparison.Ordinal)
                     && string.Equals(d.FileName, dll.FileName, StringComparison.Ordinal))
                ?? throw new DependencyException(
                    DependencyFailureKind.RestoreFailed, $"备份清单缺少 DLL 条目：{dll.Arch}/{dll.FileName}");

            var path = DllTargetPath(target, dll);
            switch (entry.Kind)
            {
                case DependencyBackupKind.Symlink:
                    {
                        var linkTarget = currentProtonRoot is not null
                            ? CurrentProtonDllPath(currentProtonRoot, dll)
                            : entry.LinkTarget
                              ?? throw new DependencyException(
                                  DependencyFailureKind.RestoreFailed,
                                  $"备份清单缺少链接目标：{dll.Arch}/{dll.FileName}");
                        // 记录的相对链接目标相对链接所在目录解析（存在性检查不能按启动器 cwd 解析）
                        var targetForExistence = Path.IsPathRooted(linkTarget)
                            ? linkTarget
                            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, linkTarget));
                        if (!File.Exists(targetForExistence))
                        {
                            throw new DependencyException(
                                DependencyFailureKind.RestoreFailed,
                                $"链接目标不存在，拒绝创建悬空链接：{linkTarget}");
                        }

                        DeleteDllPath(path);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.CreateSymbolicLink(path, linkTarget);
                        break;
                    }
                case DependencyBackupKind.File:
                    {
                        var backupPath = Path.Combine(
                            DependencyPaths.BackupDirectory(target.StateDirectory, manifest.Id),
                            entry.BackupFileName
                            ?? throw new DependencyException(
                                DependencyFailureKind.RestoreFailed,
                                $"备份清单缺少文件副本名：{dll.Arch}/{dll.FileName}"));
                        if (!File.Exists(backupPath))
                        {
                            throw new DependencyException(
                                DependencyFailureKind.RestoreFailed, $"备份文件不存在：{backupPath}");
                        }

                        DeleteDllPath(path);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.Copy(backupPath, path, overwrite: true);
                        if (entry.Sha256 is not null
                            && !string.Equals(
                                Hashing.Sha256Hex(path), entry.Sha256, StringComparison.Ordinal))
                        {
                            throw new DependencyException(
                                DependencyFailureKind.RestoreFailed,
                                $"还原后的 DLL SHA-256 与备份记录不符：{path}");
                        }

                        break;
                    }
                case DependencyBackupKind.Missing:
                    DeleteDllPath(path);
                    break;
                default:
                    throw new DependencyException(
                        DependencyFailureKind.RestoreFailed, $"未知的备份形态：{entry.Kind}");
            }
        }
    }

    /// <summary>删除目标位置的 DLL（符号链接只删链接本身；目录占位视为还原失败，不递归删用户数据）。</summary>
    private static void DeleteDllPath(string path)
    {
        if (Directory.Exists(path) && new FileInfo(path).LinkTarget is null)
        {
            throw new IOException($"目标路径是目录而非 DLL：{path}");
        }

        File.Delete(path);
    }

    /// <summary>Proton 当前树里某 DLL 的路径（x64 → x86_64-windows，x86 → i386-windows）。</summary>
    private static string CurrentProtonDllPath(string protonRoot, DependencyDll dll)
    {
        var architectureDirectory = dll.Arch == "x64" ? "x86_64-windows" : "i386-windows";
        return Path.Combine(
            protonRoot, "files", "lib", "wine", architectureDirectory, dll.FileName);
    }

    /// <summary>
    /// 恢复覆盖：有旧值 → reg add 写回；原本无值/无键 → reg delete（值不存在视为成功）。
    /// 删除失败时复查整键（值确实不在 = 成功；注册表本身不可读 = RestoreFailed）。
    /// </summary>
    private async Task RestoreOverridesAsync(
        WinePrefixTarget target, DependencyBackupManifest backup, CancellationToken cancellationToken)
    {
        foreach (var entry in backup.Overrides)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.OldValue is not null)
            {
                var restore = await RunWineRegAsync(
                    target,
                    ["reg", "add", DllOverridesKey, "/v", entry.Key, "/t", "REG_SZ", "/d", entry.OldValue, "/f"],
                    RegistryTimeoutMilliseconds,
                    cancellationToken).ConfigureAwait(false);
                if (!restore.Succeeded)
                {
                    throw new DependencyException(
                        DependencyFailureKind.RestoreFailed,
                        $"恢复 DllOverrides {entry.Key} 失败（退出码 {restore.ExitCode}）：{restore.StandardError}");
                }

                continue;
            }

            var delete = await RunWineRegAsync(
                target,
                ["reg", "delete", DllOverridesKey, "/v", entry.Key, "/f"],
                RegistryTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            if (delete.Succeeded)
            {
                continue;
            }

            if (await ValueIsAbsentAsync(target, entry.Key, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            throw new DependencyException(
                DependencyFailureKind.RestoreFailed,
                $"删除 DllOverrides {entry.Key} 失败（退出码 {delete.ExitCode}）：{delete.StandardError}");
        }
    }

    /// <summary>删除失败后的复核：值/键确实不存在才算成功；注册表读取失败返回 false。</summary>
    private async Task<bool> ValueIsAbsentAsync(
        WinePrefixTarget target, string key, CancellationToken cancellationToken)
    {
        var query = await RunWineRegAsync(
            target, ["reg", "query", DllOverridesKey], RegistryTimeoutMilliseconds, cancellationToken)
            .ConfigureAwait(false);
        if (query.Succeeded)
        {
            return !ParseRegValues(query.StandardOutput).ContainsKey(key);
        }

        var probe = await RunWineRegAsync(
            target, ["reg", "query", WineRegistryKey], RegistryTimeoutMilliseconds, cancellationToken)
            .ConfigureAwait(false);
        return probe.Succeeded; // 父键可读 = DllOverrides 键不存在 → 值自然不存在
    }

    /// <summary>执行 wine reg 命令（argv 形态；WINEPREFIX 指向目标 prefix）。</summary>
    private async Task<ProcessResult> RunWineRegAsync(
        WinePrefixTarget target,
        IReadOnlyList<string> arguments,
        int timeoutMilliseconds,
        CancellationToken cancellationToken) =>
        await processRunner.RunAsync(
            new ProcessStartSpec(
                target.WineExecutable,
                string.Empty,
                Environment: WineEnvironment(target),
                TimeoutMilliseconds: timeoutMilliseconds,
                ArgumentList: arguments),
            cancellationToken).ConfigureAwait(false);

    /// <summary>DLL 目标路径：x64 → system32，x86 → syswow64（仅 win64 prefix 有意义）。</summary>
    private static string DllTargetPath(WinePrefixTarget target, DependencyDll dll)
    {
        var directory = dll.Arch == "x64" ? "system32" : "syswow64";
        return Path.Combine(
            target.WinePrefixDirectory, "drive_c", "windows", directory, dll.FileName);
    }

    /// <summary>备份记录里的 SHA-256 是否合法（64 位小写十六进制）。</summary>
    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// 读取并校验备份清单：id 匹配、每个当前 DLL 条目形态可解析、每个覆盖键有记录、File 副本存在。
    /// 缺失/损坏/不完整一律返回 null（调用方按 BackupMissing 处理，绝不重备份）。
    /// </summary>
    private DependencyBackupManifest? TryLoadBackupManifest(
        string backupDirectory, DependencyManifest manifest)
    {
        try
        {
            var path = Path.Combine(backupDirectory, DependencyPaths.BackupManifestFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var backup = JsonSerializer.Deserialize<DependencyBackupManifest>(
                File.ReadAllText(path), Json.Default);
            if (backup is null
                || !string.Equals(backup.Id, manifest.Id, StringComparison.Ordinal)
                || backup.Dlls is null
                || backup.Overrides is null
                // JSON 可注入 null 元素：先归一为"清单不可用"，避免后续解引用 NRE
                || backup.Dlls.Any(d => d is null)
                || backup.Overrides.Any(o => o is null || string.IsNullOrWhiteSpace(o.Key)))
            {
                return null;
            }

            foreach (var dll in manifest.Dlls ?? [])
            {
                var entry = backup.Dlls.SingleOrDefault(
                    d => string.Equals(d.Arch, dll.Arch, StringComparison.Ordinal)
                         && string.Equals(d.FileName, dll.FileName, StringComparison.Ordinal));
                if (entry is null)
                {
                    return null;
                }

                switch (entry.Kind)
                {
                    case DependencyBackupKind.Symlink when string.IsNullOrWhiteSpace(entry.LinkTarget):
                    case DependencyBackupKind.File when string.IsNullOrWhiteSpace(entry.BackupFileName)
                        || entry.BackupFileName is "." or ".."
                        || Path.IsPathRooted(entry.BackupFileName)
                        || entry.BackupFileName.Contains('/')
                        || entry.BackupFileName.Contains('\\')
                        || !IsSha256Hex(entry.Sha256)
                        || !File.Exists(Path.Combine(backupDirectory, entry.BackupFileName)):
                    case not (DependencyBackupKind.Symlink
                        or DependencyBackupKind.File
                        or DependencyBackupKind.Missing):
                        return null;
                }
            }

            var overrideKeys = new HashSet<string>(
                backup.Overrides.Select(o => o.Key), StringComparer.Ordinal);
            return manifest.DllOverrides?.Keys.All(overrideKeys.Contains) == true ? backup : null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            logger?.LogWarning(ex, "依赖备份清单不可用：{Backup}", backupDirectory);
            return null;
        }
    }

    /// <summary>确保压缩包在共享缓存且校验通过：有效缓存直接复用，否则下载。</summary>
    private async Task<string> EnsureArchiveAsync(
        DependencyManifest manifest,
        IProgress<DependencyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var archivePath = Path.Combine(_cacheRoot, manifest.FileName ?? string.Empty);
        if (!IsCachedArchiveValid(manifest, archivePath))
        {
            progress?.Report(new DependencyProgress(DependencyPhase.Downloading, 0));
            var fraction = new FractionProgress(manifest.SizeBytes ?? 0, progress);
            try
            {
                await downloader.DownloadFileAsync(
                    new DownloadRequest(
                        manifest.DownloadUrl ?? string.Empty,
                        archivePath,
                        manifest.SizeBytes ?? 0,
                        manifest.Md5 ?? string.Empty),
                    fraction,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new DependencyException(
                    DependencyFailureKind.DownloadFailed,
                    $"依赖压缩包下载失败：{manifest.DownloadUrl}：{ex.Message}", ex);
            }
        }

        return archivePath;
    }

    /// <summary>缓存有效性：尺寸与 MD5 都一致才复用；Exists→读取之间的竞态按"无效"走重下。</summary>
    private static bool IsCachedArchiveValid(DependencyManifest manifest, string archivePath)
    {
        try
        {
            return File.Exists(archivePath)
                   && manifest.SizeBytes is { } size
                   && new FileInfo(archivePath).Length == size
                   && manifest.Md5 is { } md5
                   && Hashing.Md5Hex(archivePath).Equals(md5, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>解压到暂存目录并返回清单声明的字体文件绝对路径；任一文件缺失按 ExtractFailed 失败。</summary>
    private List<string> ExtractFonts(DependencyManifest manifest, string archivePath, string staging)
    {
        try
        {
            PackageInstallerService.ExtractArchive(archivePath, staging, manifest.FileName ?? string.Empty);
        }
        catch (UpdateException ex)
        {
            throw new DependencyException(
                DependencyFailureKind.ExtractFailed,
                $"依赖压缩包解压失败：{manifest.FileName}：{ex.Message}", ex);
        }

        var fonts = manifest.Fonts ?? [];
        var files = new List<string>(fonts.Count);
        foreach (var font in fonts)
        {
            var path = Path.Combine(staging, font.File);
            if (!File.Exists(path))
            {
                throw new DependencyException(
                    DependencyFailureKind.ExtractFailed,
                    $"压缩包内缺少清单声明的字体文件：{font.File}");
            }

            files.Add(path);
        }

        return files;
    }

    /// <summary>字体拷进 windows/Fonts（覆盖写：同版本重装幂等）。</summary>
    private static void CopyFonts(WinePrefixTarget target, DependencyManifest manifest, List<string> stagedFiles)
    {
        try
        {
            Directory.CreateDirectory(target.FontsDirectory);
            foreach (var (font, staged) in (manifest.Fonts ?? []).Zip(stagedFiles))
            {
                File.Copy(staged, Path.Combine(target.FontsDirectory, font.File), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DependencyException(
                DependencyFailureKind.FontCopyFailed,
                $"字体写入 prefix 失败：{ex.Message}", ex);
        }
    }

    /// <summary>生成 .reg（UTF-16LE）并单次 <c>wine reg import</c> 导入；失败按 RegistryFailed。</summary>
    private async Task ImportRegistryAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        CancellationToken cancellationToken)
    {
        var regPath = Path.Combine(target.StateDirectory, $"{manifest.Id}.reg");
        try
        {
            Directory.CreateDirectory(target.StateDirectory);
            await WineRegistryScriptBuilder.WriteAsync(
                regPath, WineRegistryScriptBuilder.Build(manifest), cancellationToken).ConfigureAwait(false);

            ProcessResult result;
            try
            {
                result = await processRunner.RunAsync(new ProcessStartSpec(
                    target.WineExecutable,
                    $"reg import \"{regPath}\"",
                    Environment: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["WINEPREFIX"] = target.WinePrefixDirectory,
                        ["WINEDLLOVERRIDES"] = SuppressMonoGeckoOverride,
                    },
                    TimeoutMilliseconds: RegistryTimeoutMilliseconds), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new DependencyException(
                    DependencyFailureKind.RegistryFailed,
                    $"wine reg import 启动失败：{ex.Message}", ex);
            }

            if (!result.Succeeded)
            {
                throw new DependencyException(
                    DependencyFailureKind.RegistryFailed,
                    $"wine reg import 退出码 {result.ExitCode}：{result.StandardError}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DependencyException(
                DependencyFailureKind.RegistryFailed,
                $"注册表脚本写入失败：{ex.Message}", ex);
        }
        finally
        {
            FileUtilities.DeleteQuiet(regPath);
        }
    }

    /// <summary>原子写完成标记（内容 id@version）；失败按 StateWriteFailed。</summary>
    private static async Task WriteMarkerAsync(WinePrefixTarget target, DependencyManifest manifest)
    {
        try
        {
            await FileUtilities.WriteAtomicAsync(
                DependencyPaths.MarkerPath(target.StateDirectory, manifest.Id),
                MarkerContent(manifest)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DependencyException(
                DependencyFailureKind.StateWriteFailed,
                $"写入安装状态失败：{ex.Message}", ex);
        }
    }

    private static string MarkerContent(DependencyManifest manifest) => $"{manifest.Id}@{manifest.Version}";

    /// <summary>
    /// 清理 winetricks 账本里该 verb 的行（token 精确匹配，保留 `vcrun2022` 这类包含子串的 verb）。
    /// 失败只告警：winetricks 自己的 `--force` 语义不依赖账本；按 winetricks 源码，`installed_file1`
    /// 是 verb 元数据变量而非 prefix 文件（2026-10-27 核对自带 winetricks）。
    /// </summary>
    private void TryCleanWinetricksLedger(WinePrefixTarget target, DependencyManifest manifest)
    {
        var path = Path.Combine(target.WinePrefixDirectory, WinetricksLedgerFileName);
        try
        {
            if (!File.Exists(path) || manifest.WinetricksVerb is not { } verb)
            {
                return;
            }

            var lines = File.ReadAllLines(path);
            var kept = lines
                .Where(line => !line.Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(verb, StringComparer.Ordinal))
                .ToArray();
            if (kept.Length == lines.Length)
            {
                return;
            }

            if (kept.Length == 0)
            {
                // 账本只剩本 verb 的行：删除文件，完整还原安装前形态
                File.Delete(path);
                return;
            }

            FileUtilities.WriteAtomic(path, string.Join('\n', kept) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "清理 winetricks 账本失败（仅警告，不阻断还原）：{Path}", path);
        }
    }

    /// <summary>从 wine 路径推导 Proton 根（…/files/bin/wine 形态，取该标记之前的部分）；非 Proton 布局返回 null。</summary>
    private static string? TryDeriveProtonRoot(string wineExecutable)
    {
        var path = Path.GetFullPath(wineExecutable);
        var marker = $"{Path.DirectorySeparatorChar}files{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var index = path.LastIndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? null : path[..index];
    }

    /// <summary>把下载器报的绝对字节数折算成 [0,1] 进度。</summary>
    private sealed class FractionProgress(long totalBytes, IProgress<DependencyProgress>? progress) : IProgress<long>
    {
        public void Report(long value)
        {
            if (totalBytes > 0)
            {
                progress?.Report(new DependencyProgress(
                    DependencyPhase.Downloading,
                    Math.Clamp((double)value / totalBytes, 0, 1)));
            }
        }
    }
}
