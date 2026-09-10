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

    /// <summary>出站网络代理模式（跟随系统/直连/手动指定）。</summary>
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;

    /// <summary>手动代理服务器地址（ProxyMode=Manual 时生效，如 http://127.0.0.1:7890）。</summary>
    public string? ProxyAddress { get; set; }

    /// <summary>配置结构版本：升级启动器时据此做一次性迁移（如补全新增的官方服务器）。</summary>
    public int SchemaVersion { get; set; }

    /// <summary>上次关闭时的窗口宽度（DIP）；空 = 使用 XAML 默认尺寸。窗口关闭时自动写入，设置页不展示。</summary>
    public int? WindowWidth { get; set; }

    /// <summary>上次关闭时的窗口高度（DIP）；空 = 使用 XAML 默认尺寸。</summary>
    public int? WindowHeight { get; set; }

    /// <summary>上次关闭时窗口是否处于最大化状态。</summary>
    public bool WindowMaximized { get; set; }
}

/// <summary>出站网络代理模式。</summary>
public enum ProxyMode
{
    /// <summary>跟随系统代理设置（默认，与未提供该设置前行为一致）。</summary>
    System,

    /// <summary>直连（显式绕过任何系统代理）。</summary>
    None,

    /// <summary>使用用户指定的代理服务器地址（http://host:port）。</summary>
    Manual,
}
