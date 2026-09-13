using Avalonia;
using Avalonia.Controls;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 窗口"视觉最大化"判定。Avalonia 12.1 实验性 Wayland 后端会把合成器的平铺状态也上报为
/// <see cref="WindowState.Maximized"/>（2026-09 Hyprland 实测：工作区 2560×1440、平铺窗口
/// 2516×1352——四周各留 22px gap——仍报 Maximized），按最大化去除圆角的样式因此会在
/// 平铺/浮动窗口上误生效。真最大化的窗口客户区应与所在屏幕工作区一致，平铺窗口则差
/// 2×合成器 gap；据此做尺寸校验：<see cref="WindowState.Maximized"/> 且客户区与工作区
/// （±容差）吻合才算视觉最大化。屏幕信息拿不到（headless 等）时退回属性语义，不改变行为。
/// </summary>
internal static class WindowStateMapper
{
    /// <summary>尺寸匹配容差（DIP）：真最大化可能有 1-2px 舍入偏差；平铺窗口差值为
    /// 2×合成器 gap（Hyprland 默认 8px 起，实测 22px），4px 容差足够区分两者。</summary>
    internal const double SizeTolerance = 4;

    /// <summary>
    /// 判断窗口是否"视觉上"最大化（即应套用铺满/去圆角样式）。
    /// </summary>
    /// <param name="state">当前窗口状态；仅 <see cref="WindowState.Maximized"/> 参与校验，
    /// 其余状态（含 FullScreen）一律 false——现状样式也只随最大化切换。</param>
    /// <param name="clientSize">窗口客户区尺寸（DIP）。</param>
    /// <param name="workingArea">所在屏幕工作区；null（拿不到屏幕）时退回属性语义返回 true。</param>
    /// <param name="scaling">所在屏幕缩放；null/非正数时工作区视为已按 DIP 上报。</param>
    /// <returns>true 表示按最大化处理窗口样式。</returns>
    internal static bool IsVisuallyMaximized(WindowState state, Size clientSize, PixelRect? workingArea, double? scaling)
    {
        if (state != WindowState.Maximized)
        {
            return false;
        }

        if (workingArea is not { } area)
        {
            return true; // 拿不到屏幕信息：信任属性语义（X11/Windows 的 Maximized 上报可靠）
        }

        // 工作区单位因后端而异：X11/Windows 按物理像素上报（需除以缩放换 DIP），实验性
        // Wayland 后端直接按 DIP 上报且缩放恒报 1——两种候选都参与匹配，谁吻合算谁。
        var dipScale = scaling is > 0 and not double.NaN ? scaling.Value : 1;
        return Matches(clientSize, area.Width, area.Height)
               || Matches(clientSize, area.Width / dipScale, area.Height / dipScale);
    }

    /// <summary>客户区宽高均落在容差内视为与工作区吻合。</summary>
    private static bool Matches(Size client, double width, double height)
        => Math.Abs(client.Width - width) <= SizeTolerance
           && Math.Abs(client.Height - height) <= SizeTolerance;
}
