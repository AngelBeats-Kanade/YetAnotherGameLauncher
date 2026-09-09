using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>基于 System.Diagnostics.Process 的进程运行器：捕获输出、超时杀死、传播取消。</summary>
public sealed class SystemProcessRunner(ILogger<SystemProcessRunner>? logger = null) : IProcessRunner
{
    /// <summary>Windows 错误码 740：ERROR_ELEVATION_REQUIRED（可执行文件清单要求管理员权限）。</summary>
    private const int ErrorElevationRequired = 740;

    /// <summary>
    /// 启动进程并等待退出，捕获 stdout/stderr；超时或取消时杀死整个进程树并抛出取消。
    /// WaitForExit=false 时即启即走（游戏启动用）：不重定向输出、不等待、不受超时影响，
    /// 并挂进程退出观察把"退出码 + 存活时长"写入日志（游戏秒退可据此排查）。
    /// </summary>
    public async Task<ProcessResult> RunAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
    {
        var waitForExit = spec.WaitForExit;
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            Arguments = spec.Arguments,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            // 即启即走时无人读取管道，重定向会因缓冲区写满卡死目标进程
            RedirectStandardOutput = waitForExit,
            RedirectStandardError = waitForExit,
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

        using var process = new Process { StartInfo = startInfo };
        var startedAt = Environment.TickCount64;
        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (!waitForExit
            && ex.NativeErrorCode == ErrorElevationRequired
            && OperatingSystem.IsWindows())
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

            process.StartInfo = new ProcessStartInfo
            {
                FileName = spec.FileName,
                Arguments = spec.Arguments,
                WorkingDirectory = startInfo.WorkingDirectory,
                UseShellExecute = true,
                CreateNoWindow = true,
            };
            process.Start();
            logger?.LogInformation("Process {File} started via shell execute (elevation prompt)", spec.FileName);
        }

        if (!waitForExit)
        {
            // 游戏秒退诊断：退出码与存活时长落日志（句柄随 using 释放，事件里只读必要字段）
            var file = spec.FileName;
            var startedTicks = startedAt;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                var exitCode = 0L;
                try
                {
                    exitCode = process.ExitCode;
                }
                catch (Exception)
                {
                    // 句柄可能已释放
                }

                logger?.LogInformation("Launched process exited after {Seconds:F1}s with code {ExitCode}: {File}",
                    (Environment.TickCount64 - startedTicks) / 1000.0, exitCode, file);
            };
            return new ProcessResult(0, "", "");
        }

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
}
