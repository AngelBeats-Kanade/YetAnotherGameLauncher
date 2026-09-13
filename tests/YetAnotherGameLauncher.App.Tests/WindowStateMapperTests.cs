using Avalonia;
using Avalonia.Controls;
using YetAnotherGameLauncher.Services;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 视觉最大化决策表：实验性 Wayland 后端把 Hyprland 平铺状态也上报为
/// <see cref="WindowState.Maximized"/>（2026-09 实测：平铺窗口 2516×1352、工作区
/// 2560×1440 仍报 Maximized），去圆角样式因此误生效——详情页左上角圆角丢失即此故。
/// 判定必须满足「状态为 Maximized 且客户区与屏幕工作区吻合（±4px，双单位候选）」；
/// 屏幕信息拿不到时退回属性语义（true）。
/// </summary>
public class WindowStateMapperTests
{
    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Minimized)]
    [InlineData(WindowState.FullScreen)]
    public void NonMaximizedStates_AreNeverVisuallyMaximized(WindowState state)
    {
        // 状态语义与屏幕几何无关：样式只应随最大化切换（FullScreen 维持现状不处理）
        Assert.False(WindowStateMapper.IsVisuallyMaximized(
            state, new Size(2560, 1440), new PixelRect(0, 0, 2560, 1440), 1));
    }

    [Fact]
    public void Maximized_WithoutScreenInfo_FallsBackToPropertySemantics()
    {
        // headless 等场景拿不到屏幕：信任属性语义，不改变 X11/Windows 上的既有行为
        Assert.True(WindowStateMapper.IsVisuallyMaximized(
            WindowState.Maximized, new Size(1464, 720), null, null));
    }

    [Fact]
    public void Maximized_ClientExactlyCoversWorkArea_InDipUnits_IsMaximized()
    {
        // Wayland 实验后端：工作区按 DIP 上报且缩放恒报 1（2026-09 实测）
        Assert.True(WindowStateMapper.IsVisuallyMaximized(
            WindowState.Maximized, new Size(2560, 1440), new PixelRect(0, 0, 2560, 1440), 1));
    }

    [Fact]
    public void Maximized_ClientCoversWorkArea_InPhysicalUnits_IsMaximized()
    {
        // X11/Windows 惯例：工作区按物理像素上报，需除以缩放换 DIP（4K@1.5 → 2560×1440）
        Assert.True(WindowStateMapper.IsVisuallyMaximized(
            WindowState.Maximized, new Size(2560, 1440), new PixelRect(0, 0, 3840, 2160), 1.5));
    }

    [Theory]
    [InlineData(2516, 1352, 2560, 1440, 1.0)]   // Wayland 后端 DIP 报法（Hyprland gap 22 实测）
    [InlineData(2516, 1352, 3840, 2160, 1.5)]   // X11/Windows 物理像素报法
    [InlineData(1464, 720, 2560, 1440, 1.0)]    // 持久化最大化恢复后窗口仍浮动的形态
    [InlineData(2544, 1424, 2560, 1440, 1.0)]   // 极小合成器 gap（8px）下的平铺
    public void Maximized_TiledOrFloatingWindow_KeepsCorners(
        double clientWidth, double clientHeight, double areaWidth, double areaHeight, double scaling)
    {
        // 平铺窗口与工作区四周差 2×gap（≥16px），远超 4px 容差：不是视觉最大化
        Assert.False(WindowStateMapper.IsVisuallyMaximized(
            WindowState.Maximized, new Size(clientWidth, clientHeight),
            new PixelRect(0, 0, (int)areaWidth, (int)areaHeight), scaling));
    }

    [Theory]
    [InlineData(2558, 1440)]   // 宽度差 2px
    [InlineData(2560, 1442)]   // 高度差 2px
    [InlineData(2558, 1438)]   // 双向各差 2px
    public void Maximized_WithinRoundingTolerance_IsMaximized(double width, double height)
    {
        // 真最大化可能有 1-2px 舍入偏差（X11 边框/WM 计算），容差内仍视为铺满
        Assert.True(WindowStateMapper.IsVisuallyMaximized(
            WindowState.Maximized, new Size(width, height), new PixelRect(0, 0, 2560, 1440), 1));
    }

    [Fact]
    public void Maximized_OnlyOneDimensionMatches_IsNotMaximized()
    {
        // 宽吻合高不吻合（如纵向平铺只占半屏）：必须宽高同时吻合
        Assert.False(WindowStateMapper.IsVisuallyMaximized(
            WindowState.Maximized, new Size(2560, 720), new PixelRect(0, 0, 2560, 1440), 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(double.NaN)]
    public void Maximized_InvalidScaling_TreatsWorkAreaAsDip(double? scaling)
    {
        // 缩放缺失/非法时工作区视为已按 DIP 上报（与第一候选一致），匹配不失效
        Assert.True(WindowStateMapper.IsVisuallyMaximized(
            WindowState.Maximized, new Size(2560, 1440), new PixelRect(0, 0, 2560, 1440), scaling));
    }
}
