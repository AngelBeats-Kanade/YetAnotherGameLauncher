namespace YetAnotherGameLauncher.Core.Models;

/// <summary>启动器全局设置。</summary>
public sealed class AppSettings
{
    /// <summary>游戏安装根目录，游戏 InstallDir 相对于它（支持绝对路径）。</summary>
    public string InstallRoot { get; set; } = "";

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>下载时的最大文件级并发数。</summary>
    public int MaxParallelDownloads { get; set; } = 8;

    /// <summary>全局限速（字节/秒），0 = 不限速。</summary>
    public long DownloadSpeedLimitBytes { get; set; }

    public string Language { get; set; } = "zh-CN";

    /// <summary>侧栏是否展开（false = 收起为图标窄条）。</summary>
    public bool SidebarExpanded { get; set; } = true;

    /// <summary>配置结构版本：升级启动器时据此做一次性迁移（如补全新增的官方服务器）。</summary>
    public int SchemaVersion { get; set; }
}
