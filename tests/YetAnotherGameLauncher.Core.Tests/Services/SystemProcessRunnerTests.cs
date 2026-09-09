using System.Diagnostics;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class SystemProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_FireAndForget_ReturnsBeforeProcessExits()
    {
        // 游戏启动场景：即启即走——不能等待长进程退出（否则 10 分钟超时会杀掉整个游戏进程树）
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("ping", "127.0.0.1 -n 30")
            : ("sleep", "30");

        var runner = new SystemProcessRunner();
        var stopwatch = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            new ProcessStartSpec(fileName, arguments, WaitForExit: false));
        stopwatch.Stop();

        Assert.Equal(0, result.ExitCode);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"fire-and-forget 应立即返回，实际耗时 {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task RunAsync_WaitForExit_ReturnsExitCode()
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd", "/c exit 7")
            : ("sh", "-c \"exit 7\"");

        var result = await new SystemProcessRunner().RunAsync(
            new ProcessStartSpec(fileName, arguments));

        Assert.Equal(7, result.ExitCode);
    }
}
