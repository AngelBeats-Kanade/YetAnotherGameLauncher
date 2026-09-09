using YetAnotherGameLauncher.Services;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// PTS 实时节拍器：首帧立即渲染、delay 按时间戳换算、回卷/卡顿大幅落后时重定基线、
/// 等待量钳制非负。背景视频此前解码多快播多快（硬解下数倍速快进），节拍器是根因修复。
/// </summary>
public class PlaybackClockTests
{
    [Fact]
    public void FirstFrame_RendersImmediately_NextFrameEntersPacing()
    {
        var clock = new PlaybackClock();

        // 首帧：立即渲染
        Assert.Null(clock.WaitDelayMs(0));
        // 同一时刻的后续帧：等待量归零（时刻已到）而非继续立即放行
        var same = clock.WaitDelayMs(0);
        Assert.NotNull(same);
        Assert.True(same.Value <= 1);
    }

    [Fact]
    public async Task Delay_MapsPtsToWallClock()
    {
        var clock = new PlaybackClock();

        // 基线帧
        Assert.Null(clock.WaitDelayMs(0));

        // 2 秒后的帧：立即查询应给出接近 2000ms 的正等待量
        var delay = clock.WaitDelayMs(2.0);
        Assert.NotNull(delay);
        Assert.InRange(delay.Value, 1900, 2000);

        // 实际等待约 2 秒后，同帧的等待量应归零（时刻已到）
        await Task.Delay(2050);
        Assert.Equal(0, clock.WaitDelayMs(2.0));
    }

    [Fact]
    public void LargeLag_RebasesInsteadOfBursting()
    {
        var clock = new PlaybackClock();

        // 基线取高 pts（播放到 10 秒处）
        Assert.Null(clock.WaitDelayMs(10));
        // EOF 回卷后 pts 归零：落后远超阈值 → 重定基线立即渲染，不爆发追帧
        Assert.Null(clock.WaitDelayMs(0));
        // 重定基线后的下一帧恢复正常节拍
        var next = clock.WaitDelayMs(0.1);
        Assert.NotNull(next);
        Assert.InRange(next.Value, 50, 100);
    }

    [Fact]
    public void Reset_ClearsBaseline()
    {
        var clock = new PlaybackClock();
        Assert.Null(clock.WaitDelayMs(10));

        clock.Reset();
        // 重置后重新取基线：任何 pts 都立即渲染
        Assert.Null(clock.WaitDelayMs(0));
    }

    [Fact]
    public void SlightlyBehind_ClampsToZero()
    {
        var clock = new PlaybackClock();
        Assert.Null(clock.WaitDelayMs(0));

        // 已过期但落后在阈值内的时刻钳制为 0 而非负数
        var behind = clock.WaitDelayMs(-0.05);
        Assert.NotNull(behind);
        Assert.Equal(0, behind.Value);
    }

    [Fact]
    public void Rate_ScalesWaitTime()
    {
        // 2 倍速：0.5 秒的 pts 间隔只需约 250ms 等待
        var clock = new PlaybackClock(rate: 2.0);
        Assert.Null(clock.WaitDelayMs(0));

        var delay = clock.WaitDelayMs(0.5);
        Assert.NotNull(delay);
        Assert.InRange(delay.Value, 150, 250);
    }
}
