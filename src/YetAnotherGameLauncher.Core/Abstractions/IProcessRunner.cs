namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>进程启动描述。</summary>
public sealed record ProcessStartSpec(
    string FileName,
    string Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    int TimeoutMilliseconds = 600_000,
    bool WaitForExit = true,
    string? OutputLogPath = null);

/// <summary>进程执行结果。</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>进程运行器抽象，便于在测试中替身化外部工具（如 hpatchz）。</summary>
public interface IProcessRunner
{
    /// <summary>运行进程至退出，返回退出码与输出；超时或取消时终止进程。</summary>
    Task<ProcessResult> RunAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default);
}
