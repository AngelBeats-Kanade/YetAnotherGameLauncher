using Xunit;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 下载速度平滑器直测（RF-2，2026-10-02）：ManualTimeProvider 注入虚拟时钟，
/// 确定性验证采样节奏、EMA、字节回退重置与跨操作 Reset——此前速度样本跨操作残留，
/// 新一轮操作首帧可能瞬时显示上一轮速度。
/// </summary>
public class DownloadSpeedSmootherTests
{
    [Fact]
    public void Samples_AtLeastMinimumInterval_EmaSmoothed()
    {
        var clock = new ManualTimeProvider(timestampFrequency: 1_000_000_000);
        var smoother = new DownloadSpeedSmoother(clock);

        smoother.OnProgressBytes(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        smoother.OnProgressBytes(1_000_000);
        Assert.Equal(1_000_000, smoother.BytesPerSecond, 0); // 首样本直接取瞬时值

        clock.Advance(TimeSpan.FromSeconds(1));
        smoother.OnProgressBytes(3_000_000); // 本秒瞬时 2MB/s → EMA = 0.6×1M + 0.4×2M
        Assert.Equal(1_400_000, smoother.BytesPerSecond, 3);
    }

    [Fact]
    public void Samples_BelowMinimumInterval_Ignored()
    {
        var clock = new ManualTimeProvider(timestampFrequency: 1_000_000_000);
        var smoother = new DownloadSpeedSmoother(clock);

        smoother.OnProgressBytes(0);
        clock.Advance(TimeSpan.FromMilliseconds(100)); // < 0.4s：不采样不更新
        smoother.OnProgressBytes(500_000);

        Assert.Equal(0, smoother.BytesPerSecond);
    }

    [Fact]
    public void ByteRollback_ResetsSpeed()
    {
        var clock = new ManualTimeProvider(timestampFrequency: 1_000_000_000);
        var smoother = new DownloadSpeedSmoother(clock);

        smoother.OnProgressBytes(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        smoother.OnProgressBytes(1_000_000);
        Assert.Equal(1_000_000, smoother.BytesPerSecond, 0);

        smoother.OnProgressBytes(200_000); // 校验失败重下：字节回退
        Assert.Equal(0, smoother.BytesPerSecond);
    }

    [Fact]
    public void Reset_ClearsResidualSpeed_AcrossOperations()
    {
        // RF-2 核心场景：上一轮操作留下了有效速度样本，新一轮开始（复位点 Reset）后
        // 首帧必须显示"—"（速度 0），不得瞬时残留旧速度
        var clock = new ManualTimeProvider(timestampFrequency: 1_000_000_000);
        var smoother = new DownloadSpeedSmoother(clock);

        smoother.OnProgressBytes(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        smoother.OnProgressBytes(1_000_000);
        Assert.True(smoother.BytesPerSecond > 0);

        smoother.Reset();

        Assert.Equal(0, smoother.BytesPerSecond);
        smoother.OnProgressBytes(100); // 新一轮首帧（间隔按重置后时间戳计）
        Assert.Equal(0, smoother.BytesPerSecond);
    }
}
