using YetAnotherGameLauncher.Services;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 解码韧性熔断器：连续无输出包判死（阈值须高于 h264 B 帧重排深度）、成功输出清零计数、
/// 连续无效回停止播放、有效回与预卷健康交接清零。背景：GPU 解码栈失效（退出期平台拆除、
/// 驱动重置）时每包无输出，FFmpeg 以每帧两条的速度刷 stderr，需熔断止损。
/// </summary>
public class DecodeGuardTests
{
    [Fact]
    public void ConsecutivePacketsWithoutFrame_TripAtThreshold()
    {
        var guard = new DecodeGuard();

        // 阈值内（合法 B 帧重排深度）：不判死
        for (var i = 0; i < DecodeGuard.MaxPacketsWithoutFrame - 1; i++)
        {
            Assert.False(guard.OnPacketWithoutFrame());
        }

        // 第 MaxPacketsWithoutFrame 个连续无输出包：判死
        Assert.True(guard.OnPacketWithoutFrame());
    }

    [Fact]
    public void DecodedFrame_ResetsStallCounter()
    {
        var guard = new DecodeGuard();

        for (var i = 0; i < DecodeGuard.MaxPacketsWithoutFrame - 1; i++)
        {
            guard.OnPacketWithoutFrame();
        }

        // 正常 B 帧重排下出帧即清零：后续再数 N 个包也不判死
        guard.OnFrameDecoded();
        for (var i = 0; i < DecodeGuard.MaxPacketsWithoutFrame - 1; i++)
        {
            Assert.False(guard.OnPacketWithoutFrame());
        }
    }

    [Fact]
    public void ViablePass_ResetsDeadPassCount()
    {
        var guard = new DecodeGuard();
        var fps = 30.0;

        // 两回未达有效标准（30fps 下有效线 = max(8, 7) = 8 帧）
        Assert.False(guard.OnPassEnd(0, fps));
        Assert.False(guard.OnPassEnd(DecodeGuard.MinViablePassFrames(fps) - 1, fps));

        // 有效回（≥8 帧）清零失败计数：之后要再连续 3 回无效才停播（第 3 回即返回 true）
        Assert.False(guard.OnPassEnd(DecodeGuard.MinViablePassFrames(fps), fps));
        Assert.False(guard.OnPassEnd(0, fps));
        Assert.False(guard.OnPassEnd(0, fps));
        Assert.True(guard.OnPassEnd(0, fps));
    }

    [Fact]
    public void DeadPasses_TripAtThirdConsecutive()
    {
        var guard = new DecodeGuard();

        Assert.False(guard.OnPassEnd(0, 30.0));
        Assert.False(guard.OnPassEnd(0, 30.0));
        // 连续第 3 回无效：停止播放
        Assert.True(guard.OnPassEnd(0, 30.0));
    }

    [Fact]
    public void Reset_ClearsDeadPassCount()
    {
        var guard = new DecodeGuard();

        Assert.False(guard.OnPassEnd(0, 30.0));
        Assert.False(guard.OnPassEnd(0, 30.0));
        guard.Reset(); // 预卷源健康接管：失败计数清零

        Assert.False(guard.OnPassEnd(0, 30.0));
        Assert.False(guard.OnPassEnd(0, 30.0));
        Assert.True(guard.OnPassEnd(0, 30.0));
    }

    [Fact]
    public void MinViablePassFrames_FpsIndependentFloorAndScale()
    {
        // 帧率未知：至少 8 帧
        Assert.Equal(8, DecodeGuard.MinViablePassFrames(0));
        // 高帧率：0.25s 折算（60fps → 15 帧）
        Assert.Equal(15, DecodeGuard.MinViablePassFrames(60.0));
    }
}
