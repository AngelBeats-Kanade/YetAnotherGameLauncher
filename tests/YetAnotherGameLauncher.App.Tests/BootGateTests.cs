using Xunit;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>启动门控纯决策回归：就绪/最小展示时长/超时/无游戏/配置错误的放行矩阵。</summary>
public class BootGateTests
{
    [Fact]
    public void Ready_ReleasesAfterMinSplash()
    {
        Assert.False(BootGate.ShouldRelease(true, hasSelectedGame: true, configError: false,
            elapsedSeconds: BootGate.MinSplashSeconds - 0.01, timeoutSeconds: 5));
        Assert.True(BootGate.ShouldRelease(true, hasSelectedGame: true, configError: false,
            elapsedSeconds: BootGate.MinSplashSeconds, timeoutSeconds: 5));
    }

    [Fact]
    public void NotReady_HoldsUntilTimeout()
    {
        Assert.False(BootGate.ShouldRelease(false, hasSelectedGame: true, configError: false,
            elapsedSeconds: 4.99, timeoutSeconds: 5));
        Assert.True(BootGate.ShouldRelease(false, hasSelectedGame: true, configError: false,
            elapsedSeconds: 5, timeoutSeconds: 5));
    }

    [Fact]
    public void Timeout_IsUnconditional_EvenBeforeMinSplash()
    {
        // 小超时配置（测试注入）先于最小展示时长到达：超时无条件放行，不得被最小时长扣住
        Assert.True(BootGate.ShouldRelease(false, hasSelectedGame: true, configError: false,
            elapsedSeconds: 0.3, timeoutSeconds: 0.2));
    }

    [Fact]
    public void NoSelectedGame_ReleasesAfterMinSplash()
    {
        // 无游戏无可等资产：到最小时长即放行（ready 快照本就为 true，此处验证决策不受 hasGame 干扰）
        Assert.True(BootGate.ShouldRelease(true, hasSelectedGame: false, configError: false,
            elapsedSeconds: BootGate.MinSplashSeconds, timeoutSeconds: 5));
        Assert.False(BootGate.ShouldRelease(false, hasSelectedGame: false, configError: false,
            elapsedSeconds: BootGate.MinSplashSeconds - 0.01, timeoutSeconds: 5));
    }

    [Fact]
    public void ConfigError_ReleasesAfterMinSplash_WithoutAssets()
    {
        Assert.True(BootGate.ShouldRelease(false, hasSelectedGame: true, configError: true,
            elapsedSeconds: BootGate.MinSplashSeconds, timeoutSeconds: 5));
    }

    [Fact]
    public void CustomMinSplash_IsHonored()
    {
        Assert.True(BootGate.ShouldRelease(true, hasSelectedGame: true, configError: false,
            elapsedSeconds: 0.05, timeoutSeconds: 5, minSplashSeconds: 0.05));
    }
}
