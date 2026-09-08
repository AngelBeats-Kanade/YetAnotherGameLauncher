using System.Diagnostics;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>测试用的时间源：手动推进虚拟时钟（SpeedLimiter 等基于 TimeProvider 的服务用）。</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public ManualTimeProvider(long initialTicks = 0) => _ticks = initialTicks;

    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan delta) => _ticks += delta.Ticks;
}
