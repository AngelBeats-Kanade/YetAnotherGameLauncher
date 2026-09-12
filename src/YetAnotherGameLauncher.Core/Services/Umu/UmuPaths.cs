namespace YetAnotherGameLauncher.Core.Services.Umu;

/// <summary>
/// 原生 umu 组件的磁盘布局（与上游 umu-launcher 的 XDG 约定对齐）。
/// 路径均可注入，便于测试；缺省尊重 XDG_DATA_HOME / XDG_CACHE_HOME。
/// </summary>
public static class UmuPaths
{
    /// <summary>Steam Runtime 与 umu 本地数据根：~/.local/share/umu。</summary>
    public static string LocalRoot(string? dataHome = null) =>
        Path.Combine(dataHome ?? DefaultDataHome(), "umu");

    /// <summary>Steam Runtime 安装目录：~/.local/share/umu/&lt;variant&gt;（如 steamrt4）。</summary>
    public static string RuntimeDirectory(string variant, string? dataHome = null) =>
        Path.Combine(LocalRoot(dataHome), variant);

    /// <summary>兼容工具（GE-Proton / UMU-Proton）安装根：~/.local/share/Steam/compatibilitytools.d。</summary>
    public static string SteamCompatRoot(string? dataHome = null) =>
        Path.Combine(dataHome ?? DefaultDataHome(), "Steam", "compatibilitytools.d");

    /// <summary>下载缓存：~/.cache/umu。</summary>
    public static string CacheRoot(string? cacheHome = null) =>
        Path.Combine(cacheHome ?? DefaultCacheHome(), "umu");

    /// <summary>组件安装互斥锁目录：{data}/yagl/umu-locks。</summary>
    public static string LockDirectory(string? dataDirectory = null) =>
        Path.Combine(dataDirectory ?? AppPaths.DataDirectory, "umu-locks");

    /// <summary>指定名称的锁文件路径。</summary>
    public static string LockFile(string name, string? dataDirectory = null) =>
        Path.Combine(LockDirectory(dataDirectory), name);

    /// <summary>Runtime 安装完成标记文件名（与上游 umu 一致）。</summary>
    public const string InstallMarkerName = ".installed.ok";

    /// <summary>数据根缺省：Linux 尊重 XDG_DATA_HOME，否则 ~/.local/share。</summary>
    private static string DefaultDataHome()
    {
        if (OperatingSystem.IsWindows())
        {
            return AppPaths.DataHomeDirectory;
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdg) || !Path.IsPathRooted(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
    }

    /// <summary>缓存根缺省：Linux 尊重 XDG_CACHE_HOME，否则 ~/.cache。</summary>
    private static string DefaultCacheHome()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(AppPaths.DataHomeDirectory, "cache");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return string.IsNullOrWhiteSpace(xdg) || !Path.IsPathRooted(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : xdg;
    }
}
