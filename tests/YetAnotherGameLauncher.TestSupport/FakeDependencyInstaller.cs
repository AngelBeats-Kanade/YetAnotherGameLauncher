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

    /// <summary>注入失败分类；null = 成功。</summary>
    public DependencyFailureKind? FailKind { get; set; }

    /// <summary>GetState 是否报告已安装（版本固定取清单 Version）。</summary>
    public bool MarkInstalled { get; set; }

    /// <summary>true = InstallAsync 挂到 <see cref="ReleaseInstall"/> 才完成（构造忙碌窗口用）。</summary>
    public bool HangOnInstall { get; set; }

    /// <summary>注入未分类原始异常（分类学兜底测试用）；优先于 FailKind。</summary>
    public Exception? ThrowRaw { get; set; }

    private TaskCompletionSource? _hangGate;

    /// <summary>放行被 <see cref="HangOnInstall"/> 挂起的安装。</summary>
    public void ReleaseInstall() => _hangGate?.TrySetResult();

    public DependencyInstallState GetState(WinePrefixTarget target, DependencyManifest manifest) =>
        MarkInstalled
            ? new DependencyInstallState(true, manifest.Version)
            : new DependencyInstallState(false, null);

    public async Task InstallAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        IProgress<DependencyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        InstallCount++;
        ReceivedTarget = target;
        if (HangOnInstall)
        {
            _hangGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _hangGate.Task;
        }

        if (ThrowRaw is { } raw)
        {
            throw raw;
        }

        if (FailKind is { } kind)
        {
            throw new DependencyException(kind, "fake failure");
        }

        MarkInstalled = true;
    }
}
