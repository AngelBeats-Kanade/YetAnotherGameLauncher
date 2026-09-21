using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;

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
    public async Task RunAsync_FireAndForget_WritesOutputToLogFile()
    {
        // 启动日志：即启即走的进程输出（stdout/stderr）要落盘，秒退/报错可据此排查。
        // 审计修复（2026-09-19）：原实现 Windows 直接 return（零断言静默绿）；现双腿各自真实断言。
        using var tempDir = new TestSupport.TempDir();
        var logPath = tempDir.FilePath("logs", "launch-test.log");
        var runner = new SystemProcessRunner();

        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c echo out-line& echo err-line 1>&2& exit /b 7")
            : ("/bin/sh", "-c \"echo out-line; echo err-line >&2; exit 7\"");

        var result = await runner.RunAsync(new ProcessStartSpec(
            fileName,
            arguments,
            WaitForExit: false,
            OutputLogPath: logPath));

        Assert.Equal(0, result.ExitCode); // 即启即走恒 0
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string content = "";
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(logPath))
            {
                content = await ReadAllTextSharedAsync(logPath);
                if (content.Contains("exited with code 7", StringComparison.Ordinal))
                {
                    break; // 退出脚注落盘 = 输出已排空
                }
            }

            await Task.Delay(100);
        }

        Assert.Contains(
            OperatingSystem.IsWindows() ? "# command: cmd.exe" : "# command: /bin/sh",
            content, StringComparison.Ordinal);
        Assert.Contains("out-line", content, StringComparison.Ordinal);
        Assert.Contains("[stderr] err-line", content, StringComparison.Ordinal);
        Assert.Contains("exited with code 7", content, StringComparison.Ordinal);
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
    public async Task RunAsync_Timeout_KillsProcessAndThrowsUpdateException()
    {
        // 回归（2026-09-20）：超时曾重抛裸 OCE，消费端把一切 OCE 当"用户取消"静默吞，
        // 大包补丁超时被杀后 UI 无任何提示。超时必须抛 UpdateException（可区分的失败）。
        // 长 sleep + 极短超时
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("ping", "127.0.0.1 -n 30")
            : ("sleep", "30");

        var runner = new SystemProcessRunner();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<UpdateException>(() => runner.RunAsync(
            new ProcessStartSpec(fileName, arguments, TimeoutMilliseconds: 500)));

        stopwatch.Stop();
        Assert.Contains("timed out", ex.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"超时应及时触发，实际 {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task RunAsync_UserCancellation_StillThrowsOperationCanceled()
    {
        // 守卫：超时改抛 UpdateException 不得误伤用户取消路径——取消 token 仍传播 OCE
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("ping", "127.0.0.1 -n 30")
            : ("sleep", "30");

        var runner = new SystemProcessRunner();
        using var cts = new CancellationTokenSource(500);
        cts.CancelAfter(500);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new ProcessStartSpec(fileName, arguments, TimeoutMilliseconds: 60_000), cts.Token));
    }

    [Fact]
    public async Task RunAsync_WaitForExit_ChildReceivesCustomEnvironment()
    {
        // 审计缺口（2026-09-19）：等待模式 + 自定义环境变量——注入循环须真正生效到子进程
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c echo %YAGL_TEST_VAR%")
            : ("/bin/sh", "-c \"printf '%s' \"$YAGL_TEST_VAR\"\"");

        var result = await new SystemProcessRunner().RunAsync(new ProcessStartSpec(
            fileName,
            arguments,
            Environment: new Dictionary<string, string> { ["YAGL_TEST_VAR"] = "injected-value" }));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("injected-value", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task RunAsync_FireAndForget_WithEnvironment_LogsEnvBlock()
    {
        // 审计缺口（2026-09-19）：即启即走 + 输出日志 + 自定义环境——日志头要带 env 块（秒退排查线索）
        using var tempDir = new TempDir();
        var logPath = tempDir.FilePath("logs", "launch-env.log");
        var runner = new SystemProcessRunner();

        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c echo done& exit /b 7")
            : ("/bin/sh", "-c \"echo done; exit 7\"");

        await runner.RunAsync(new ProcessStartSpec(
            fileName,
            arguments,
            WaitForExit: false,
            Environment: new Dictionary<string, string>
            {
                ["YAGL_TEST_VAR"] = "injected-value",
                ["WINEDEBUG"] = "-all",
            },
            OutputLogPath: logPath));

        var content = await WaitForLogFootnoteAsync(logPath, "exited with code 7");

        Assert.Contains("# environment:", content, StringComparison.Ordinal);
        Assert.Contains("#   YAGL_TEST_VAR=injected-value", content, StringComparison.Ordinal);
        Assert.Contains("#   WINEDEBUG=-all", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_StartFailure_WithLogPath_WritesHeaderThenReleasesWriter()
    {
        // 审计缺口（2026-09-19）：启动失败时已打开的日志句柄要释放——文件落盘即证明 Dispose
        // （AutoFlush 已写头部，若句柄泄漏文件会被独占到进程退出）
        using var tempDir = new TempDir();
        var logPath = tempDir.FilePath("logs", "launch-fail.log");
        var runner = new SystemProcessRunner();

        await Assert.ThrowsAnyAsync<Win32Exception>(() => runner.RunAsync(new ProcessStartSpec(
            "/nonexistent/yagl-missing-binary",
            "-dx11",
            WaitForExit: false,
            OutputLogPath: logPath)));

        var content = await File.ReadAllTextAsync(logPath); // 能读到 = 写入方已释放
        Assert.Contains("# YAGL launch log", content, StringComparison.Ordinal);
        Assert.Contains("# command: /nonexistent/yagl-missing-binary -dx11", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FireAndForget_LongLivedProcess_ExitObservedViaExitedEvent()
    {
        // 审计缺口（2026-09-19）：布防后进程仍在运行 → Exited 事件分支（与 HasExited 补查对称）。
        // 睡 0.5s 保证 EnableRaisingEvents 时进程必然存活，退出脚注经事件链落盘
        using var tempDir = new TempDir();
        var logPath = tempDir.FilePath("logs", "launch-exited.log");
        var runner = new SystemProcessRunner();

        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c ping -n 2 127.0.0.1 >nul & exit /b 5")
            : ("/bin/sh", "-c \"sleep 0.5; exit 5\"");

        var result = await runner.RunAsync(new ProcessStartSpec(
            fileName, arguments, WaitForExit: false, OutputLogPath: logPath));

        Assert.Equal(0, result.ExitCode); // 即启即走恒 0
        var content = await WaitForLogFootnoteAsync(logPath, "exited with code 5");
        Assert.Contains(
            OperatingSystem.IsWindows() ? "# command: cmd.exe" : "# command: /bin/sh",
            content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FireAndForget_InstantExit_NoLog_ReportsExitToAppLog()
    {
        // 审计缺口（2026-09-19）：无日志即启即走的秒退线索——退出码与存活时长写应用日志（HasExited
        // 补查与 Exited 事件两分支任一命中均写；瞬秒命令最大化命中补查分支的概率）
        var logger = new CollectingLogger();
        var runner = new SystemProcessRunner(logger);

        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c exit 0")
            : ("true", "");

        var result = await runner.RunAsync(new ProcessStartSpec(fileName, arguments, WaitForExit: false));

        Assert.Equal(0, result.ExitCode);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !logger.Messages.Any(m => m.Contains("exited after", StringComparison.Ordinal)))
        {
            await Task.Delay(50);
        }

        var exitLog = Assert.Single(logger.Messages, m => m.Contains("exited after", StringComparison.Ordinal));
        Assert.Contains("code 0", exitLog, StringComparison.Ordinal);
        Assert.Contains(fileName, exitLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FireAndForget_RequireAdministrator_ElevatesViaShellThenFails()
    {
        // Windows CI 腿（2026-09-19，审计遗留 ~45 行提权回退段）：requireAdministrator 清单的 exe
        // 在非提权会话经 CreateProcess 启动报 740 → 回退 ShellExecute（会话无 UAC 同意 UI）→ 再次失败。
        // 断言两件事：调用方收到 Win32Exception；日志留下"提权回退"说明行（防误导秒退排查）+ env 警告。
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("740 是 Windows CreateProcess 清单语义，POSIX 不可构造");
            return;
        }

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Assert.Skip("运行时已提权：requireAdministrator 桩直接启动成功，不触发 740 回退");
            return;
        }

        // 现场编译带 requireAdministrator 清单的桩 exe（不依赖系统自带可执行文件）
        var stubDir = Path.Combine(Path.GetTempPath(), "yagl-elev-stub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stubDir);
        var logPath = Path.Combine(stubDir, "launch.log");
        try
        {
            File.WriteAllText(Path.Combine(stubDir, "stub.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>disable</Nullable>
                    <ApplicationManifest>app.manifest</ApplicationManifest>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(stubDir, "app.manifest"), """
                <?xml version="1.0" encoding="utf-8"?>
                <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
                  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
                    <security>
                      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
                        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
                      </requestedPrivileges>
                    </security>
                  </trustInfo>
                </assembly>
                """);
            // 桩体即退：万一判定失误（会话其实已提权）进程也不会悬挂，测试以"未抛 740"清晰红掉
            File.WriteAllText(Path.Combine(stubDir, "Program.cs"), "System.Console.WriteLine(\"stub\");");

            var build = Process.Start(new ProcessStartInfo("dotnet", "build -c Release --nologo -v q")
            {
                WorkingDirectory = stubDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            Assert.NotNull(build);
            _ = build.StandardOutput.ReadToEndAsync();
            _ = build.StandardError.ReadToEndAsync();
            Assert.True(build.WaitForExit(180_000), "requireAdministrator 桩编译超时");
            Assert.True(build.ExitCode == 0, "requireAdministrator 桩编译失败");

            var logger = new CollectingLogger();
            var runner = new SystemProcessRunner(logger);
            var stubExe = Path.Combine(stubDir, "bin", "Release", "net10.0", "stub.exe");

            // 即启即走 + 日志 + 自定义 env：740 回退路径放弃 env 注入并写警告（ENV 循环不进 ShellExecute）
            await Assert.ThrowsAnyAsync<Win32Exception>(() => runner.RunAsync(new ProcessStartSpec(
                stubExe,
                "-run",
                WaitForExit: false,
                Environment: new Dictionary<string, string> { ["YAGL_TEST_VAR"] = "x" },
                OutputLogPath: logPath)));

            var content = await File.ReadAllTextAsync(logPath); // 能读到 = 日志句柄已释放（防泄漏防线）
            Assert.Contains("# elevated via shell (requireAdministrator)", content, StringComparison.Ordinal);
            Assert.Contains(logger.Messages, m => m.Contains("elevated via shell", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(stubDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 桩进程偶发残留占用：临时目录由系统回收，不影响判定
            }
        }
    }

    /// <summary>
    /// 以共享读写方式读取日志：Windows 上生产端 StreamWriter 持有写锁期间，
    /// File.ReadAllTextAsync 的共享模式不允许并发读（IOException），轮询必须用 FileShare.ReadWrite。
    /// </summary>
    private static async Task<string> ReadAllTextSharedAsync(string logPath)
    {
        await using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    /// <summary>轮询等待日志退出脚注落盘（输出泵收尾是异步的），返回完整日志文本。</summary>
    private static async Task<string> WaitForLogFootnoteAsync(string logPath, string footnote)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string content = "";
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(logPath))
            {
                content = await ReadAllTextSharedAsync(logPath);
                if (content.Contains(footnote, StringComparison.Ordinal))
                {
                    return content;
                }
            }

            await Task.Delay(100);
        }

        Assert.Fail($"日志 10s 内未出现脚注「{footnote}」，实际内容：{content}");
        return content;
    }

    /// <summary>收集日志消息的最小 ILogger（应用日志断言用）。</summary>
    private sealed class CollectingLogger : ILogger<SystemProcessRunner>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
