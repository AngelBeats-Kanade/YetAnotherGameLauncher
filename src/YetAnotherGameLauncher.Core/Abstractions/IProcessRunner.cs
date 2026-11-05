namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>进程启动描述。</summary>
/// <param name="FileName">可执行文件路径。</param>
/// <param name="Arguments">旧式参数串（原样交给 ProcessStartInfo.Arguments；与 ArgumentList 互斥，后者优先）。</param>
/// <param name="WorkingDirectory">工作目录；null = 当前目录。</param>
/// <param name="Environment">要注入的环境变量（覆盖继承值）。</param>
/// <param name="TimeoutMilliseconds">等待超时（毫秒）。</param>
/// <param name="WaitForExit">false = 即启即走（游戏启动）。</param>
/// <param name="OutputLogPath">即启即走的启动日志路径。</param>
/// <param name="ArgumentList">argv 形态参数（逐项原样传递，含空格路径不被拆分）；非空时不设置 Arguments。</param>
public sealed record ProcessStartSpec(
    string FileName,
    string Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    int TimeoutMilliseconds = 600_000,
    bool WaitForExit = true,
    string? OutputLogPath = null,
    IReadOnlyList<string>? ArgumentList = null);

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
