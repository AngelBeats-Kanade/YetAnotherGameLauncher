namespace YetAnotherGameLauncher.Core.Models;

/// <summary>启动器全局设置。</summary>
public sealed class AppSettings
{
    /// <summary>游戏安装根目录，游戏 InstallDir 相对于它（支持绝对路径）。</summary>
    public string InstallRoot { get; set; } = "";

    /// <summary>界面主题（跟随系统/浅色/深色）。</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>全局限速（字节/秒），0 = 不限速。</summary>
    public long DownloadSpeedLimitBytes { get; set; }

    /// <summary>界面语言（culture 名，如 "zh-CN"）。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>侧栏是否展开（false = 收起为图标窄条）。</summary>
    public bool SidebarExpanded { get; set; } = true;

    /// <summary>
    /// 应用自有背景图（设置/关于/侧栏底色）：本地文件路径或 http(s) URL。
    /// 留空 = 内置的主题感知渐变背景；游戏详情页背景不受此项影响。
    /// </summary>
    public string? AppBackgroundImage { get; set; }

    /// <summary>配置结构版本：升级启动器时据此做一次性迁移（如补全新增的官方服务器）。</summary>
    public int SchemaVersion { get; set; }
}
