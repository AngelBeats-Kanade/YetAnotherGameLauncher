using System.Diagnostics;
using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>基于 System.Diagnostics.Process 的进程运行器：捕获输出、超时杀死、传播取消。</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    /// <summary>启动进程并等待退出，捕获 stdout/stderr；超时或取消时杀死整个进程树并抛出取消。</summary>
    public async Task<ProcessResult> RunAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            Arguments = spec.Arguments,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (spec.Environment is not null)
        {
            foreach (var (key, value) in spec.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

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
