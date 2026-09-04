namespace YetAnotherGameLauncher.Core.Models;

/// <summary>启动器全局设置。</summary>
public sealed class AppSettings
{
    /// <summary>游戏安装根目录，游戏 InstallDir 相对于它（支持绝对路径）。</summary>
    public string InstallRoot { get; set; } = "";

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>下载时的最大文件级并发数。</summary>
    public int MaxParallelDownloads { get; set; } = 8;

    public string Language { get; set; } = "zh-CN";
}
