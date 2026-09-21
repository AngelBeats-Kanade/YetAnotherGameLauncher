using Xunit;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>启动门控纯决策回归：视频优先/海报宽限/最小展示时长/超时/无游戏/配置错误的放行矩阵。</summary>
public class BootGateTests
{
    [Fact]
    public void VideoReady_ReleasesAfterMinSplash()
    {
        Assert.False(BootGate.ShouldRelease(videoReady: true, posterReady: false, hasSelectedGame: true,
            configError: false, elapsedSeconds: BootGate.MinSplashSeconds - 0.01, timeoutSeconds: 5));
        Assert.True(BootGate.ShouldRelease(videoReady: true, posterReady: false, hasSelectedGame: true,
            configError: false, elapsedSeconds: BootGate.MinSplashSeconds, timeoutSeconds: 5));
    }

    [Fact]
    public void PosterOnly_HoldsUntilGrace_EvenThoughMinSplashPassed()
    {
        // 观感核心：海报先到不立即放行（否则进入后还要看一次静态→视频跳变）——
        // 宽限期内持续压住，给视频首帧留出胜出窗口
        Assert.False(BootGate.ShouldRelease(videoReady: false, posterReady: true, hasSelectedGame: true,
            configError: false, elapsedSeconds: BootGate.MinSplashSeconds, timeoutSeconds: 5));
        Assert.False(BootGate.ShouldRelease(videoReady: false, posterReady: true, hasSelectedGame: true,
            configError: false, elapsedSeconds: BootGate.PosterGraceSeconds - 0.01, timeoutSeconds: 5));
        Assert.True(BootGate.ShouldRelease(videoReady: false, posterReady: true, hasSelectedGame: true,
            configError: false, elapsedSeconds: BootGate.PosterGraceSeconds, timeoutSeconds: 5));
    }

    [Fact]
    public void VideoLate_ButBeforeGrace_WinsOverPoster()
    {
        // 海报已就绪且过了最小时长，视频在宽限期内到达：立即放行（视频优先）
        Assert.True(BootGate.ShouldRelease(videoReady: true, posterReady: true, hasSelectedGame: true,
            configError: false, elapsedSeconds: BootGate.PosterGraceSeconds - 0.01, timeoutSeconds: 5));
    }

    [Fact]
    public void NothingReady_HoldsUntilTimeout()
    {
        Assert.False(BootGate.ShouldRelease(videoReady: false, posterReady: false, hasSelectedGame: true,
            configError: false, elapsedSeconds: 4.99, timeoutSeconds: 5));
        Assert.True(BootGate.ShouldRelease(videoReady: false, posterReady: false, hasSelectedGame: true,
            configError: false, elapsedSeconds: 5, timeoutSeconds: 5));
    }

    [Fact]
    public void Timeout_IsUnconditional_EvenBeforeMinSplash()
    {
        // 小超时配置（测试注入）先于最小展示时长到达：超时无条件放行，不得被最小时长扣住
        Assert.True(BootGate.ShouldRelease(videoReady: false, posterReady: false, hasSelectedGame: true,
            configError: false, elapsedSeconds: 0.3, timeoutSeconds: 0.2));
    }

    [Fact]
    public void Timeout_AlsoShortCircuits_PosterGrace()
    {
        // 超时短于宽限：超时优先（慢网络首启不该被宽限再拖一截）
        Assert.True(BootGate.ShouldRelease(videoReady: false, posterReady: true, hasSelectedGame: true,
            configError: false, elapsedSeconds: 1.0, timeoutSeconds: 0.9,
            posterGraceSeconds: 1.5));
    }

    [Fact]
    public void NoSelectedGame_ReleasesAfterMinSplash()
    {
        // 无游戏无可等资产：到最小时长即放行
        Assert.True(BootGate.ShouldRelease(videoReady: false, posterReady: false, hasSelectedGame: false,
            configError: false, elapsedSeconds: BootGate.MinSplashSeconds, timeoutSeconds: 5));
        Assert.False(BootGate.ShouldRelease(videoReady: false, posterReady: false, hasSelectedGame: false,
            configError: false, elapsedSeconds: BootGate.MinSplashSeconds - 0.01, timeoutSeconds: 5));
    }

    [Fact]
    public void ConfigError_ReleasesAfterMinSplash_WithoutAssets()
    {
        Assert.True(BootGate.ShouldRelease(videoReady: false, posterReady: false, hasSelectedGame: true,
            configError: true, elapsedSeconds: BootGate.MinSplashSeconds, timeoutSeconds: 5));
    }

    [Fact]
    public void CustomMinSplash_AndGrace_AreHonored()
    {
        Assert.True(BootGate.ShouldRelease(videoReady: true, posterReady: false, hasSelectedGame: true,
            configError: false, elapsedSeconds: 0.05, timeoutSeconds: 5, minSplashSeconds: 0.05));
        Assert.True(BootGate.ShouldRelease(videoReady: false, posterReady: true, hasSelectedGame: true,
            configError: false, elapsedSeconds: 0.3, timeoutSeconds: 5,
            minSplashSeconds: 0.05, posterGraceSeconds: 0.3));
    }
}
