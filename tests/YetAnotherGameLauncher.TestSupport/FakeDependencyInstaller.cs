using YetAnotherGameLauncher.Core.Dependencies;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>依赖安装器假实现：记录目标与调用数，状态/失败/挂起均可编程（依赖区 VM、视觉树与截图测试共用）。</summary>
public sealed class FakeDependencyInstaller : IDependencyInstaller
{
    public IReadOnlyList<DependencyManifest> Dependencies { get; } =
        DependencyCatalog.LoadEmbedded();

    /// <summary>最近一次 InstallAsync 收到的目标（断言解析结果用）。</summary>
    public WinePrefixTarget? ReceivedTarget { get; private set; }

    /// <summary>是否发起过安装。</summary>
    public bool InstallRequested => InstallCount > 0;

    /// <summary>InstallAsync 调用次数（忙门断言用）。</summary>
    public int InstallCount { get; private set; }

    /// <summary>最近一次 UninstallAsync 收到的目标。</summary>
    public WinePrefixTarget? ReceivedUninstallTarget { get; private set; }

    /// <summary>是否发起过还原。</summary>
    public bool UninstallRequested => UninstallCount > 0;

    /// <summary>UninstallAsync 调用次数（重装=先卸载后安装的接线断言用）。</summary>
    public int UninstallCount { get; private set; }

    /// <summary>操作顺序（"install" / "uninstall"），重装=先卸载后安装的顺序断言用。</summary>
    public List<string> CallLog { get; } = [];

    /// <summary>注入失败分类；null = 成功。</summary>
    public DependencyFailureKind? FailKind { get; set; }

    /// <summary>注入还原失败分类；null = 成功。</summary>
    public DependencyFailureKind? FailUninstallKind { get; set; }

    /// <summary>GetState 是否报告已安装（版本固定取清单 Version）。</summary>
    public bool MarkInstalled { get; set; }

    /// <summary>GetState 状态覆盖（Damaged/BackupMissing/NeedsRecovery 等 UI 态测试用）；null = 由 MarkInstalled 推导。</summary>
    public DependencyStateKind? StateOverride { get; set; }

    /// <summary>已安装但覆盖值校验暂不可用（超时）的 UI 文案测试用（全部条目）。</summary>
    public bool VerificationUnavailable { get; set; }

    /// <summary>只对指定依赖报告覆盖值校验不可用（多条目混合态截图用）。</summary>
    public HashSet<string> VerificationUnavailableIds { get; } = new(StringComparer.Ordinal);

    /// <summary>按依赖 id 的状态覆盖（优先于 <see cref="StateOverride"/>；截图/多条目混合态用）。</summary>
    public Dictionary<string, DependencyStateKind> StateOverrides { get; } = new(StringComparer.Ordinal);

    /// <summary>true = InstallAsync 挂到 <see cref="ReleaseInstall"/> 才完成（构造忙碌窗口用）。</summary>
    public bool HangOnInstall { get; set; }

    /// <summary>true = UninstallAsync 挂到 <see cref="ReleaseUninstall"/> 才完成。</summary>
    public bool HangOnUninstall { get; set; }

    /// <summary>注入未分类原始异常（分类学兜底测试用）；优先于 FailKind。</summary>
    public Exception? ThrowRaw { get; set; }

    /// <summary>注入卸载阶段未分类原始异常；优先于 FailUninstallKind。</summary>
    public Exception? UninstallThrowRaw { get; set; }

    /// <summary>GetState 调用钩子（断言调用线程/次数用）。</summary>
    public Action? OnGetState { get; set; }

    private TaskCompletionSource? _hangGate;
    private TaskCompletionSource? _uninstallGate;

    /// <summary>放行被 <see cref="HangOnInstall"/> 挂起的安装。</summary>
    public void ReleaseInstall() => _hangGate?.TrySetResult();

    /// <summary>放行被 <see cref="HangOnUninstall"/> 挂起的还原。</summary>
    public void ReleaseUninstall() => _uninstallGate?.TrySetResult();

    public DependencyInstallState GetState(WinePrefixTarget target, DependencyManifest manifest)
    {
        OnGetState?.Invoke();
        var unverified = VerificationUnavailable
                         || VerificationUnavailableIds.Contains(manifest.Id);
        if (StateOverrides.TryGetValue(manifest.Id, out var overridden))
        {
            return new DependencyInstallState(
                overridden,
                overridden == DependencyStateKind.Installed ? manifest.Version : null,
                overridden == DependencyStateKind.Installed && unverified);
        }

        if (StateOverride is { } kind)
        {
            return kind == DependencyStateKind.Installed
                ? new DependencyInstallState(
                    DependencyStateKind.Installed, manifest.Version, unverified)
                : new DependencyInstallState(kind, null);
        }

        return MarkInstalled
            ? new DependencyInstallState(
                DependencyStateKind.Installed, manifest.Version, unverified)
            : DependencyInstallState.NotInstalled;
    }

    public async Task InstallAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        IProgress<DependencyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        InstallCount++;
        CallLog.Add("install");
        ReceivedTarget = target;
        if (HangOnInstall)
        {
            progress?.Report(new DependencyProgress(DependencyPhase.Installing, null));
            _hangGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // F50①/D2：挂起期间响应取消令牌（镜像真实安装器的可取消语义）——
            // VM 未接线令牌时本等待对 Cancel 不动，取消测试据此变红
            await _hangGate.Task.WaitAsync(cancellationToken);
        }

        CancelledObserved = cancellationToken.IsCancellationRequested;

        if (ThrowRaw is { } raw)
        {
            throw raw;
        }

        if (FailKind is { } kind)
        {
            throw new DependencyException(kind, "fake failure");
        }

        MarkInstalled = true;
        StateOverride = null;
    }

    public async Task UninstallAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        CancellationToken cancellationToken = default)
    {
        UninstallCount++;
        CallLog.Add("uninstall");
        ReceivedUninstallTarget = target;
        if (HangOnUninstall)
        {
            _uninstallGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _uninstallGate.Task.WaitAsync(cancellationToken);
        }

        if (UninstallThrowRaw is { } raw)
        {
            throw raw;
        }

        if (FailUninstallKind is { } kind)
        {
            throw new DependencyException(kind, "fake uninstall failure");
        }

        MarkInstalled = false;
        StateOverride = null;
    }

    /// <summary>InstallAsync 收尾时观察到的令牌取消状态（D2 令牌接线断言用）。</summary>
    public bool CancelledObserved { get; private set; }
}
