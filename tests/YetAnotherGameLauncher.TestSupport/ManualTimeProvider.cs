namespace YetAnotherGameLauncher.TestSupport;

/// <summary>
/// 测试用的时间源：手动推进虚拟时钟（GetUtcNow 与 GetTimestamp 同步推进，SpeedLimiter 等基于 TimeProvider 的服务用）。
/// timestampFrequency 可注入非默认频率（默认 TimeSpan.TicksPerSecond=1e7 恰与 TimeSpan 单位重合，
/// 会掩蔽"QPC tick 被当 TimeSpan tick 用"类缺陷——F13 教训；Linux 真机 QPC=1e9 用 1_000_000_000 模拟）。
/// initialTicks 以 TimeSpan ticks 记账，GetTimestamp 按频率换算。
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly long _frequency;

    private long _ticks;

    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

    public ManualTimeProvider(long initialTicks = 0, long? timestampFrequency = null)
    {
        _ticks = initialTicks;
        _frequency = timestampFrequency ?? TimeSpan.TicksPerSecond;
    }

    public override long GetTimestamp() => _ticks * _frequency / TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long TimestampFrequency => _frequency;

    public void Advance(TimeSpan delta)
    {
        _ticks += delta.Ticks;
        _utcNow += delta;
    }
}
