using FFmpeg.AutoGen;
using Xunit;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 播放器纯函数决策表。预卷触发：F51——上一预卷任务未完成时必须跳过（否则新句柄覆盖旧句柄、
/// 旧任务从 Stop 的 join 中孤儿化，其原生 free 仍可与退出期平台拆除赛跑），跳过不置 kicked、
/// 本圈稍后帧重试。解码器输出模式：像素格式在首帧 get_format 才协商（打开瞬间恒
/// AV_PIX_FMT_NONE），"挂了硬解设备却协商出软格式"（nvidia-vaapi-driver EGL 模式特征）必须
/// 与普通软解区分。AVPixelFormat 为托管枚举，本类不触原生库。
/// </summary>
public class VideoBackdropPlayerDecisionTests
{
    [Fact]
    public void ShouldKickPreroll_DecisionTable()
    {
        // 已触发过 / 无效 pts / 无有效循环终点 / 窗口外 → false
        Assert.False(FfmpegVideoBackdropPlayer.ShouldKickPreroll(
            kicked: true, ptsSeconds: 1, loopEnd: 10, triggerWindowSeconds: 2, inFlightPreroll: null));
        Assert.False(FfmpegVideoBackdropPlayer.ShouldKickPreroll(
            kicked: false, ptsSeconds: double.NaN, loopEnd: 10, triggerWindowSeconds: 2, inFlightPreroll: null));
        Assert.False(FfmpegVideoBackdropPlayer.ShouldKickPreroll(
            kicked: false, ptsSeconds: 1, loopEnd: 0, triggerWindowSeconds: 2, inFlightPreroll: null));
        Assert.False(FfmpegVideoBackdropPlayer.ShouldKickPreroll(
            kicked: false, ptsSeconds: 1, loopEnd: 10, triggerWindowSeconds: 2, inFlightPreroll: null));

        // 窗口内（含边界：距终点恰为窗口即触发）：无在途预卷 / 在途已完成 → true
        Assert.True(FfmpegVideoBackdropPlayer.ShouldKickPreroll(
            kicked: false, ptsSeconds: 8, loopEnd: 10, triggerWindowSeconds: 2, inFlightPreroll: null));
        Assert.True(FfmpegVideoBackdropPlayer.ShouldKickPreroll(
            kicked: false, ptsSeconds: 8, loopEnd: 10, triggerWindowSeconds: 2, inFlightPreroll: Task.CompletedTask));

        // F51 核心规则：在途预卷未完成 → false（调用方不置 kicked，本圈稍后帧重试）
        Assert.False(FfmpegVideoBackdropPlayer.ShouldKickPreroll(
            kicked: false, ptsSeconds: 8, loopEnd: 10, triggerWindowSeconds: 2, inFlightPreroll: new Task(() => { })));
    }

    [Fact]
    public void DescribeDecoderOutputMode_DecisionTable()
    {
        // 硬件格式 → hardware（与设备是否在手上无关，格式即事实）
        Assert.Equal("hardware", FfmpegVideoBackdropPlayer.DescribeDecoderOutputMode(
            AVPixelFormat.AV_PIX_FMT_VAAPI, hwDeviceAttached: true));
        Assert.Equal("hardware", FfmpegVideoBackdropPlayer.DescribeDecoderOutputMode(
            AVPixelFormat.AV_PIX_FMT_CUDA, hwDeviceAttached: true));
        Assert.Equal("hardware", FfmpegVideoBackdropPlayer.DescribeDecoderOutputMode(
            AVPixelFormat.AV_PIX_FMT_D3D11, hwDeviceAttached: false));

        // 挂了硬解设备却协商出软格式：nvidia-vaapi-driver EGL 模式的特征信号，必须与普通软解区分
        Assert.Contains("hw device", FfmpegVideoBackdropPlayer.DescribeDecoderOutputMode(
            AVPixelFormat.AV_PIX_FMT_YUV420P, hwDeviceAttached: true));
        Assert.Contains("hw device", FfmpegVideoBackdropPlayer.DescribeDecoderOutputMode(
            AVPixelFormat.AV_PIX_FMT_NONE, hwDeviceAttached: true));

        // 无设备软解 → 普通 software
        Assert.Equal("software", FfmpegVideoBackdropPlayer.DescribeDecoderOutputMode(
            AVPixelFormat.AV_PIX_FMT_YUV420P, hwDeviceAttached: false));
    }
}
