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

    [Fact]
    public void CreateElevatedStartInfo_KeepsIdentityAndSwitchesToShellExecute()
    {
        // 提权回退：换 ShellExecute 语义、保留文件名/参数/工作目录，且绝不携带自定义环境
        var original = new ProcessStartInfo
        {
            FileName = "D:/games/game.exe",
            Arguments = "-dx11",
            WorkingDirectory = "D:/games",
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        original.Environment["STEAM_COMPAT_DATA_PATH"] = "/x";

        var elevated = SystemProcessRunner.CreateElevatedStartInfo(original);

        Assert.True(elevated.UseShellExecute);
        Assert.Equal("D:/games/game.exe", elevated.FileName);
        Assert.Equal("-dx11", elevated.Arguments);
        Assert.Equal("D:/games", elevated.WorkingDirectory);
        Assert.False(elevated.Environment.ContainsKey("STEAM_COMPAT_DATA_PATH")); // 自定义环境不随提权路径注入
    }

    [Fact]
    public async Task RunAsync_Timeout_KillsProcessAndReportsCancellation()
    {
        // 等待模式超时：杀进程树并抛取消（长 sleep + 极短超时）
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("ping", "127.0.0.1 -n 30")
            : ("sleep", "30");

        var runner = new SystemProcessRunner();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new ProcessStartSpec(fileName, arguments, TimeoutMilliseconds: 500)));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"超时应及时触发，实际 {stopwatch.Elapsed}");
    }
}
