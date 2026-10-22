using System.Diagnostics;
using Xunit;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.Core.Utilities;
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
        Assert.True(result.HelperStarted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WaitAsync_Error_ReturnsExitCodeAndMessage()
    {
        var path = _temp.FilePath("status.txt");
        File.WriteAllText(path, "ERROR:4:game CreateProcess failed");

        var result = await BridgeLaunchStatus.WaitAsync(path, TimeSpan.FromSeconds(1));

        Assert.Equal(BridgeLaunchState.Failed, result.State);
        Assert.True(result.HelperStarted);
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
        Assert.True(result.HelperStarted);
    }

    [Fact]
    public async Task WaitAsync_Cancellation_CleansStatusFile()
    {
        // 取消（用户关闭/切换）也要清掉占位文件；否则反复启动会积累 status-*.txt
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
        Assert.True(result.HelperStarted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WaitAsync_HelperStartsAfterReadyTimeout_StillWaitsForStartupWindow()
    {
        // 两段计时（2026-10-21）：helper 还没写出首个状态时，READY 上限不能开始计时；
        // 否则容器冷启动稍慢（但仍在启动窗口内）会被误报成桥故障。
        var path = _temp.FilePath("status.txt");
        File.WriteAllText(path, string.Empty);

        var wait = BridgeLaunchStatus.WaitAsync(
            path,
            timeout: TimeSpan.FromMilliseconds(500),
            startupTimeout: TimeSpan.FromSeconds(5));
        await Task.Delay(1000);
        File.WriteAllText(path, "STARTING");
        await Task.Delay(100);
        File.WriteAllText(path, DecoyParentBridge.ReadyStatus);

        var result = await wait;

        Assert.Equal(BridgeLaunchState.Ready, result.State);
        Assert.True(result.HelperStarted);
    }

    [Fact]
    public async Task WaitAsync_StartupTimeoutOmitted_StartupPhaseUsesTimeout()
    {
        // 省略 startupTimeout = 只配置单一超时：启动段沿用 timeout，而不是静默取 10 s 默认值。
        // 否则调用方注入短超时后仍可能等满默认启动预算（2026-10-21 review 的 API 脚枪）。
        var path = _temp.FilePath("status.txt");
        File.WriteAllText(path, string.Empty);
        var logger = new CapturingLogger();
        var stopwatch = Stopwatch.StartNew();

        var result = await BridgeLaunchStatus.WaitAsync(
            path, TimeSpan.FromMilliseconds(100), logger: logger);

        stopwatch.Stop();
        Assert.Equal(BridgeLaunchState.TimedOut, result.State);
        Assert.False(result.HelperStarted);
        Assert.False(File.Exists(path));
        // 只断言 TimedOut 无法区分“用了 100 ms”与“静默用了 10 s 默认启动预算”，
        // 因此钉住耗时上限（M84 变异守卫）。
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"启动段应使用 100 ms 注入值，实际耗时 {stopwatch.Elapsed.TotalMilliseconds:0} ms");
        // 真启动超时时状态文件始终为空，first status 行不会出现；超时路径必须自己留下
        // elapsed/limit，否则用户报「未启动」时日志里没有可核对的边界。
        Assert.Contains(
            logger.Entries,
            entry => entry.Message.Contains("startup timeout", StringComparison.Ordinal)
                && entry.Message.Contains("ms", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WaitAsync_StartupTimeoutOmitted_ReadyAfterTotalBudget_TimesOut()
    {
        // 省略 startupTimeout 时 timeout 是总预算：STARTING 出现后 READY 段只能使用剩余时间，
        // 不能重置出第二个完整 timeout。STARTING 在 1 s 出现时，旧实现把截止时间推到 3 s，
        // READY 在 2.5 s 会被接受；共享预算应在 2 s 到点超时。时序留足调度余量。
        var path = _temp.FilePath("status.txt");
        File.WriteAllText(path, string.Empty);

        var wait = BridgeLaunchStatus.WaitAsync(path, TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromSeconds(1));
        File.WriteAllText(path, "STARTING");
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        File.WriteAllText(path, DecoyParentBridge.ReadyStatus);

        var result = await wait;

        Assert.Equal(BridgeLaunchState.TimedOut, result.State);
        Assert.True(result.HelperStarted);
    }

    [Fact]
    public async Task WaitAsync_HelperStarted_ReadyPhaseUsesConfiguredTimeout()
    {
        // 显式 startupTimeout 时，STARTING 出现后必须改用 timeout（READY 上限），
        // 不能再沿用 5 s 的启动上限。只用结果断言无法区分两者，耗时断言钉住 READY 段（M85）。
        var path = _temp.FilePath("status.txt");
        File.WriteAllText(path, "STARTING");
        var stopwatch = Stopwatch.StartNew();

        var result = await BridgeLaunchStatus.WaitAsync(
            path,
            timeout: TimeSpan.FromMilliseconds(100),
            startupTimeout: TimeSpan.FromSeconds(5));

        stopwatch.Stop();
        Assert.Equal(BridgeLaunchState.TimedOut, result.State);
        Assert.True(result.HelperStarted);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"READY 段应使用 100 ms timeout，实际耗时 {stopwatch.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public void DescribeTimeout_IncludesLaunchAndApplicationLogPaths()
    {
        // 两个目录故意不同：应用日志目录必须由调用方显式传入，不能从启动日志路径反推。
        var launchLogPath = _temp.FilePath(Path.Combine("launch-logs", "launch-game.log"));
        var appLogDirectory = _temp.FilePath("app-logs");
        var appLogPath = FileUtilities.ApplicationLogFilePath(
            appLogDirectory, DateOnly.FromDateTime(DateTime.Now));

        foreach (var helperStarted in new[] { false, true })
        {
            var message = BridgeLaunchStatus.DescribeTimeout(
                new BridgeLaunchResult(BridgeLaunchState.TimedOut, HelperStarted: helperStarted),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10),
                launchLogPath,
                appLogDirectory,
                "test reason");

            Assert.Contains(launchLogPath, message, StringComparison.Ordinal);
            Assert.Contains(appLogPath, message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Path.Combine(Path.GetDirectoryName(launchLogPath)!, "yagl-"),
                message,
                StringComparison.Ordinal);
        }
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

    [Fact]
    public void DefaultTimeouts_AreTenSecondsPerPhase()
    {
        // 2026-10-21 用户明确契约：容器冷启动 + helper 启动 10s，游戏进程创建 10s。
        Assert.Equal(TimeSpan.FromSeconds(10), BridgeLaunchStatus.DefaultStartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), BridgeLaunchStatus.DefaultTimeout);
    }

    [Fact]
    public async Task WaitAsync_FirstStatus_LogsElapsedForDiagnosis()
    {
        // 阶段边界可诊断（2026-10-21）：真机需要知道 STARTING 出现在第几毫秒，
        // 才能判断 10 秒启动预算是否擦边；这里先写 STARTING，覆盖正常诊断路径。
        var path = _temp.FilePath("status.txt");
        File.WriteAllText(path, "STARTING");
        var logger = new CapturingLogger();

        var wait = BridgeLaunchStatus.WaitAsync(
            path,
            TimeSpan.FromSeconds(1),
            startupTimeout: TimeSpan.FromSeconds(1),
            logger: logger);
        await Task.Delay(100);
        File.WriteAllText(path, DecoyParentBridge.ReadyStatus);

        var result = await wait;

        Assert.Equal(BridgeLaunchState.Ready, result.State);
        Assert.Contains(
            logger.Entries,
            entry => entry.Message.Contains("first status", StringComparison.Ordinal)
                && entry.Message.Contains("STARTING", StringComparison.Ordinal)
                && entry.Message.Contains("ms", StringComparison.Ordinal));
    }
}
