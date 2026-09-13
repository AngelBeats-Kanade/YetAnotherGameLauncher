namespace YetAnotherGameLauncher.Services;

/// <summary>
/// Linux 窗口后端选择策略。Avalonia 12.1 的原生 Wayland 后端（Avalonia.Wayland 包）是实验性的：
/// <c>UsePlatformDetect()</c> 不会自动选中它，<c>UseWayland()</c> 又没有自动回退——在无 Wayland
/// 合成器的环境里无条件启用会直接启动失败。因此仅当会话确有 Wayland 显示（WAYLAND_DISPLAY 非空）
/// 且用户未设逃生舱环境变量时才启用原生 Wayland，其余情况一律走 X11/XWayland 默认路径。
/// </summary>
internal static class WaylandBackendPolicy
{
    /// <summary>强制回退 XWayland 的逃生舱环境变量名（值取 "1"/"true" 时生效，不区分大小写）。</summary>
    public const string ForceXwaylandVariable = "YAGL_FORCE_XWAYLAND";

    /// <summary>
    /// 判断本次启动是否应使用原生 Wayland 后端。
    /// </summary>
    /// <param name="isLinux">是否运行在 Linux 上（Windows/macOS 恒走默认平台检测）。</param>
    /// <param name="waylandDisplay">WAYLAND_DISPLAY 环境变量值；空白视为无 Wayland 会话。</param>
    /// <param name="forceXwayland">YAGL_FORCE_XWAYLAND 环境变量值；"1"/"true" 时强制回退 X11。</param>
    /// <returns>true 表示启用原生 Wayland；false 表示走 X11/XWayland。</returns>
    public static bool ShouldUseNativeWayland(bool isLinux, string? waylandDisplay, string? forceXwayland)
        => isLinux
           && !string.IsNullOrWhiteSpace(waylandDisplay)
           && !IsForceXwaylandSet(forceXwayland);

    /// <summary>逃生舱值判定：仅 "1"/"true"（不区分大小写）视为设置；"0"/"false"/其他值不影响。</summary>
    private static bool IsForceXwaylandSet(string? value)
        => value is not null
           && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}
