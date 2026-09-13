using YetAnotherGameLauncher.Services;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 原生 Wayland 后端启用决策表：Avalonia 12.1 的 Wayland 后端无自动回退
/// （无 Wayland 合成器时 UseWayland() 直接启动失败），决策必须严格满足
/// 「Linux + WAYLAND_DISPLAY 非空 + 未设 YAGL_FORCE_XWAYLAND 逃生舱」。
/// </summary>
public class WaylandBackendPolicyTests
{
    [Fact]
    public void Linux_WithWaylandDisplay_UsesNativeWayland()
    {
        Assert.True(WaylandBackendPolicy.ShouldUseNativeWayland(
            isLinux: true, waylandDisplay: "wayland-0", forceXwayland: null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Linux_WithoutUsableWaylandDisplay_FallsBackToX11(string? waylandDisplay)
    {
        Assert.False(WaylandBackendPolicy.ShouldUseNativeWayland(
            isLinux: true, waylandDisplay: waylandDisplay, forceXwayland: null));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void Linux_ForceXwaylandEscapeHatch_FallsBackToX11(string forceXwayland)
    {
        Assert.False(WaylandBackendPolicy.ShouldUseNativeWayland(
            isLinux: true, waylandDisplay: "wayland-0", forceXwayland: forceXwayland));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    public void Linux_UnsetOrNegativeEscapeHatch_DoesNotAffectDecision(string? forceXwayland)
    {
        Assert.True(WaylandBackendPolicy.ShouldUseNativeWayland(
            isLinux: true, waylandDisplay: "wayland-1", forceXwayland: forceXwayland));
    }

    [Fact]
    public void NonLinux_NeverUsesNativeWayland()
    {
        // Windows 上即使残留 WAYLAND_DISPLAY（如 WSL 互操作场景）也绝不能走 Wayland 后端
        Assert.False(WaylandBackendPolicy.ShouldUseNativeWayland(
            isLinux: false, waylandDisplay: "wayland-0", forceXwayland: null));
    }

    [Fact]
    public void ForceXwaylandVariableName_IsStableContract()
    {
        // 环境变量名是对用户承诺的逃生舱契约（README/AGENTS 文档引用），改名即破坏承诺
        Assert.Equal("YAGL_FORCE_XWAYLAND", WaylandBackendPolicy.ForceXwaylandVariable);
    }
}
