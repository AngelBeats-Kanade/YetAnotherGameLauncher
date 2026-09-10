using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>基于 System.Diagnostics.Process 的进程运行器：捕获输出、超时杀死、传播取消。</summary>
/// <param name="logger">日志（进程秒退/提升回退记录）。</param>
/// <param name="supportsElevationRetry">是否支持 740 提升回退（ShellExecute 弹 UAC 为 Windows 专属行为）。</param>
public sealed class SystemProcessRunner(
    ILogger<SystemProcessRunner>? logger = null,
    bool supportsElevationRetry = true) : IProcessRunner
{
    /// <summary>Windows 错误码 740：ERROR_ELEVATION_REQUIRED（可执行文件清单要求管理员权限）。</summary>
    private const int ErrorElevationRequired = 740;

    private readonly bool _supportsElevationRetry = supportsElevationRetry;

    /// <summary>
    /// 启动进程并等待退出，捕获 stdout/stderr；超时或取消时杀死整个进程树并抛出取消。
    /// WaitForExit=false 时即启即走（游戏启动用）：不重定向输出、不等待、不受超时影响，
    /// 并挂进程退出观察把"退出码 + 存活时长"写入日志（游戏秒退可据此排查）。
    /// </summary>
    public async Task<ProcessResult> RunAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
    {
        var waitForExit = spec.WaitForExit;
        // 即启即走 + 指定了日志文件：也要重定向（有人异步消费，不会卡满缓冲区）
        var redirectOutput = waitForExit || (!waitForExit && spec.OutputLogPath is not null);
        StreamWriter? logWriter = null;
        if (!waitForExit && spec.OutputLogPath is { } logPath)
        {
            var logDir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
            logWriter.WriteLine($"# YAGL launch log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            logWriter.WriteLine($"# command: {spec.FileName} {spec.Arguments}");
            logWriter.WriteLine($"# working directory: {spec.WorkingDirectory}");
            if (spec.Environment is { Count: > 0 } env)
            {
                logWriter.WriteLine("# environment:");
                foreach (var (key, value) in env)
                {
                    logWriter.WriteLine($"#   {key}={value}");
                }
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            Arguments = spec.Arguments,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            // 即启即走时无人读取管道，重定向会因缓冲区写满卡死目标进程（写日志路径除外：异步消费）
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = true,
        };

        // 仅在确有自定义环境变量时才触碰 Environment 集合：.NET 一旦发现该集合被访问过，
        // 就会在 UseShellExecute=true 时抛 InvalidOperationException——提权回退路径会被误伤
        if (spec.Environment is { Count: > 0 })
        {
            foreach (var (key, value) in spec.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        // 带输出日志的即启即走路径会在输出泵收尾后才 Dispose 进程句柄（管道随 Dispose 关闭，
        // 提前 Dispose 会让泵立刻 EOF/异常）——所以这里不能用 using
        var process = new Process { StartInfo = startInfo };
        var startedAt = Environment.TickCount64;
        var elevated = false;
        // stdout/stderr 事件来自不同线程，StreamWriter 非线程安全，所有写入串行化
        object? logLock = logWriter is null ? null : new();
        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (!waitForExit
            && ex.NativeErrorCode == ErrorElevationRequired
            && _supportsElevationRetry)
        {
            // 游戏 exe 清单要求管理员权限（requireAdministrator）：CreateProcess 无法自提升。
            // 改走 ShellExecute 由系统弹 UAC；该路径不支持按进程注入环境变量，
            // 若配置了自定义环境只能放弃（换全新的 StartInfo，避免触发环境变量校验）
            if (spec.Environment is { Count: > 0 })
            {
                logger?.LogWarning(
                    "Launching {File} elevated via shell: {Count} custom environment variables cannot be applied",
                    spec.FileName, spec.Environment.Count);
            }

            process.StartInfo = CreateElevatedStartInfo(startInfo);
            elevated = true;
            process.Start();
            logger?.LogInformation("Process {File} started via shell execute (elevation prompt)", spec.FileName);
        }
        catch
        {
            // 启动失败也要释放已打开的日志文件，否则 handle 泄漏
            logWriter?.Dispose();
            throw;
        }

        if (!waitForExit && logWriter is not null && redirectOutput && !elevated)
        {
            // 自管输出泵：直接从管道 ReadLine 到 EOF，保证 stdout/stderr 都完整落盘。
            // 不用 BeginOutputReadLine 事件——WaitForExit() 只排空 stdout，stderr 事件会丢
            var writer = logWriter;
            var gate = logLock!;
            var pumpStdout = PumpToLog(process.StandardOutput, writer, gate, null);
            var pumpStderr = PumpToLog(process.StandardError, writer, gate, "[stderr] ");
            var startedTicksCopy = startedAt;
            var launchedFile = spec.FileName;
            process.EnableRaisingEvents = true;
            if (process.HasExited)
            {
                // 进程在布防前就退出了：Exited 事件不会再触发，直接收尾
                ObserveFireAndForgetExit(process, pumpStdout, pumpStderr, writer, gate, launchedFile, startedTicksCopy, logger);
            }
            else
            {
                process.Exited += (_, _) => ObserveFireAndForgetExit(
                    process, pumpStdout, pumpStderr, writer, gate, launchedFile, startedTicksCopy, logger);
            }

            return new ProcessResult(0, "", "");
        }

        if (!waitForExit)
        {
            // 无日志路径：仍然把"退出码 + 存活时长"写应用日志（游戏秒退排查线索）。
            // 进程句柄不主动释放：Dispose 会解除 Exited 布防导致事件丢失，交给 SafeProcessHandle 终结器
            var startedTicksCopy = startedAt;
            var launchedFile = spec.FileName;
            process.EnableRaisingEvents = true;
            if (process.HasExited)
            {
                LogFireAndForgetExit(process, startedTicksCopy, launchedFile, logger);
            }
            else
            {
                process.Exited += (_, _) => LogFireAndForgetExit(process, startedTicksCopy, launchedFile, logger);
            }

            return new ProcessResult(0, "", "");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(spec.TimeoutMilliseconds);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            var exitTask = process.WaitForExitAsync(timeoutCts.Token);

            try
            {
                await exitTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // 进程已退出
                }

                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// 构造提升（ShellExecute）启动描述：不支持按进程注入环境变量（会抛），
    /// 因此返回全新 StartInfo——文件名/参数/工作目录保留，其余（重定向等）按 ShellExecute 语义。
    /// internal 供单测（经 InternalsVisibleTo）。
    /// </summary>
    internal static ProcessStartInfo CreateElevatedStartInfo(ProcessStartInfo original) => new()
    {
        FileName = original.FileName,
        Arguments = original.Arguments,
        WorkingDirectory = original.WorkingDirectory,
        UseShellExecute = true,
        CreateNoWindow = true,
    };

    /// <summary>即启即走 + 输出日志：等两个输出泵到 EOF 后写退出脚注、关闭日志并释放进程句柄。</summary>
    private static void ObserveFireAndForgetExit(
        Process process,
        Task pumpStdout,
        Task pumpStderr,
        StreamWriter writer,
        object gate,
        string file,
        long startedTicks,
        ILogger? logger)
    {
        LogFireAndForgetExit(process, startedTicks, file, logger);
        _ = Task.Run(async () =>
        {
            // 管道在进程退出后到达 EOF，两个泵收尾后写退出脚注并关闭日志
            await Task.WhenAll(pumpStdout, pumpStderr).ConfigureAwait(false);
            var seconds = AliveSeconds(process, startedTicks, out var exitCode);
            lock (gate)
            {
                try
                {
                    writer.WriteLine($"# process exited with code {exitCode} after {seconds:F1}s");
                    writer.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            try
            {
                process.Dispose(); // 泵已结束，日志路径专用的句柄在此收尾
            }
            catch (Exception)
            {
                // 句柄可能已释放
            }
        });
    }

    /// <summary>即启即走（无输出日志）：把"退出码 + 存活时长"写入应用日志（游戏秒退排查线索）。</summary>
    private static void LogFireAndForgetExit(
        Process process, long startedTicks, string file, ILogger? logger)
    {
        var seconds = AliveSeconds(process, startedTicks, out var exitCode);
        logger?.LogInformation("Launched process exited after {Seconds:F1}s with code {ExitCode}: {File}",
            seconds, exitCode, file);
    }

    /// <summary>读取已退出进程的存活时长与退出码；句柄不可用时回退 0/0。</summary>
    private static double AliveSeconds(Process process, long startedTicks, out long exitCode)
    {
        exitCode = 0;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (Exception)
        {
            // 句柄可能已释放
        }

        return (Environment.TickCount64 - startedTicks) / 1000.0;
    }

    /// <summary>把一个输出流逐行写入日志直到 EOF；prefix 用于区分 stderr。行写入经 <paramref name="gate"/> 串行化。</summary>
    private static async Task PumpToLog(
        StreamReader reader, StreamWriter writer, object gate, string? prefix)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lock (gate)
            {
                try
                {
                    writer.WriteLine(prefix is null ? line : $"{prefix}{line}");
                }
                catch (ObjectDisposedException)
                {
                    return; // 日志已关闭：停止泵
                }
            }
        }
    }
}
