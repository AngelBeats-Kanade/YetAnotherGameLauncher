using Xunit;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services.Umu;

/// <summary>
/// 桥启动状态文件读取（2026-10-20 复审）：helper 在游戏进程创建后写 READY，创建前失败写
/// ERROR:&lt;code&gt;:&lt;message&gt;；launcher 据此区分桥故障与游戏自身退出码（例如校验拒绝的 5）。
/// </summary>
public sealed class BridgeLaunchStatusTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void CreateStatusPath_CreatesEmptyPlaceholderInBridgeRoot()
    {
        var bridgeRoot = _temp.FilePath("bridge");
        var path = BridgeLaunchStatus.CreateStatusPath(bridgeRoot);

        Assert.StartsWith(bridgeRoot + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
        Assert.Equal(string.Empty, File.ReadAllText(path));
    }

    [Fact]
    public async Task WaitAsync_Ready_ReturnsReadyAndCleansStatusFile()
    {
        var path = _temp.FilePath("status.txt");
        File.WriteAllText(path, DecoyParentBridge.ReadyStatus);

        var result = await BridgeLaunchStatus.WaitAsync(path, TimeSpan.FromSeconds(1));

        Assert.Equal(BridgeLaunchState.Ready, result.State);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WaitAsync_Error_ReturnsExitCodeAndMessage()
    {
        var path = _temp.FilePath("status.txt");
        File.WriteAllText(path, "ERROR:4:game CreateProcess failed");

        var result = await BridgeLaunchStatus.WaitAsync(path, TimeSpan.FromSeconds(1));

        Assert.Equal(BridgeLaunchState.Failed, result.State);
        Assert.Equal(4, result.ExitCode);
        Assert.Contains("game CreateProcess failed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitAsync_StartingThenReady_KeepsPollingUntilReady()
    {
        var path = _temp.FilePath("status.txt");
        File.WriteAllText(path, "STARTING");

        var wait = BridgeLaunchStatus.WaitAsync(path, TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        File.WriteAllText(path, DecoyParentBridge.ReadyStatus);

        var result = await wait;

        Assert.Equal(BridgeLaunchState.Ready, result.State);
    }

    [Fact]
    public async Task WaitAsync_Cancellation_CleansStatusFile()
    {
        // 取消（用户关闭/切换）也要清掉占位文件；否则 30 s 内反复启动会积累 status-*.txt
        var path = _temp.FilePath("status.txt");
        File.WriteAllText(path, "STARTING");
        using var cts = new CancellationTokenSource();

        var wait = BridgeLaunchStatus.WaitAsync(path, TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WaitAsync_Timeout_ReturnsTimedOut()
    {
        var path = _temp.FilePath("status.txt");
        File.WriteAllText(path, "STARTING");

        var result = await BridgeLaunchStatus.WaitAsync(path, TimeSpan.FromMilliseconds(100));

        Assert.Equal(BridgeLaunchState.TimedOut, result.State);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WaitAsync_MalformedError_ReturnsFailedWithoutCode()
    {
        var path = _temp.FilePath("status.txt");
        File.WriteAllText(path, "ERROR:no-code");

        var result = await BridgeLaunchStatus.WaitAsync(path, TimeSpan.FromSeconds(1));

        Assert.Equal(BridgeLaunchState.Failed, result.State);
        Assert.Null(result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }
}
