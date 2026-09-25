using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class SpeedLimiterTests
{
    [Fact]
    public void Acquire_Unlimited_ReturnsZero()
    {
        var limiter = new SpeedLimiter(new ManualTimeProvider());
        limiter.BytesPerSecond = 0;

        Assert.Equal(TimeSpan.Zero, limiter.Acquire(1024));
        Assert.Equal(TimeSpan.Zero, limiter.Acquire(1024));
    }

    [Fact]
    public void Acquire_QueuesBeyondBudget()
    {
        var time = new ManualTimeProvider();
        var limiter = new SpeedLimiter(time) { BytesPerSecond = 1000 };

        // 第一批立即发放
        Assert.Equal(TimeSpan.Zero, limiter.Acquire(600));

        // 第二批超出本秒预算：需等待到下一个周期
        var wait = limiter.Acquire(600);
        Assert.True(wait > TimeSpan.FromSeconds(0.5), $"wait={wait}");
        Assert.True(wait <= TimeSpan.FromSeconds(1.2), $"wait={wait}");

        // 虚拟时钟推进过排队窗口后，第三批不再等待
        time.Advance(wait + TimeSpan.FromMilliseconds(600));
        Assert.Equal(TimeSpan.Zero, limiter.Acquire(300));
    }

    [Fact]
    public void SetZero_ResetQueue()
    {
        var time = new ManualTimeProvider();
        var limiter = new SpeedLimiter(time) { BytesPerSecond = 1000 };
        limiter.Acquire(1000);

        limiter.BytesPerSecond = 0;
        Assert.Equal(TimeSpan.Zero, limiter.Acquire(4096));
    }

    [Fact]
    public void Acquire_WaitReturnedInTimestampUnits_NotAssumedTicks()
    {
        // Linux QPC 频率 = 1e9（纳秒）：默认频率 1e7 与 TimeSpan tick 恰好重合，
        // 会掩蔽"QPC tick 被当 TimeSpan tick 用"的单位错配（F13，artifacts/bugs.md）
        var time = new ManualTimeProvider(timestampFrequency: 1_000_000_000);
        var limiter = new SpeedLimiter(time) { BytesPerSecond = 1_000_000 }; // 1 MB/s

        // 0.1s 预算，首请求立即发放
        Assert.Equal(TimeSpan.Zero, limiter.Acquire(100_000));

        // 排队在预算之后：应等 ~0.1s（错配形态把 1e8 纳秒当 1e8 TimeSpan tick = 10s）
        var wait = limiter.Acquire(0);
        Assert.InRange(wait, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));
    }
}
