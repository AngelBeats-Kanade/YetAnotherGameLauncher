using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>假进程运行器：记录启动规格，返回预设结果。</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessStartSpec> Specs { get; } = [];

    public Func<ProcessStartSpec, ProcessResult> Handler { get; set; } = _ => new ProcessResult(0, "", "");

    public Task<ProcessResult> RunAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
    {
        Specs.Add(spec);
        return Task.FromResult(Handler(spec));
    }
}
