namespace YetAnotherGameLauncher.Core;

/// <summary>应用数据目录等平台相关路径。</summary>
public static class AppPaths
{
    /// <summary>配置目录：Windows 为 %APPDATA%\yagl，Linux 为 ~/.config/yagl。</summary>
    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yagl");

    /// <summary>
    /// 平台数据根目录（**不含**应用级 yagl 后缀）：Linux ~/.local/share（尊重 XDG_DATA_HOME），
    /// Windows %LOCALAPPDATA%。CompatTools.PrefixRoot 语义接收此根目录（它自己追加 yagl/prefixes），
    /// 与 <see cref="DataDirectory"/> 拼接使用，避免出现 yagl/yagl 双写。
    /// 注意：必须声明在 <see cref="DataDirectory"/> 之前——静态初始化器按声明顺序执行。
    /// </summary>
    public static string DataHomeDirectory { get; } = ResolveDataHomeRoot();

    /// <summary>
    /// 数据目录（Wine prefix 等大体积数据）：Linux 为 ~/.local/share/yagl（尊重 XDG_DATA_HOME），
    /// Windows 为 %LOCALAPPDATA%\yagl。与配置目录分离：数据可清理重建，配置不行。
    /// </summary>
    public static string DataDirectory { get; } = Path.Combine(DataHomeDirectory, "yagl");

    /// <summary>配置文件路径。环境变量 YAGL_CONFIG 可覆盖（测试/多实例场景）。</summary>
    public static string GetConfigFilePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("YAGL_CONFIG");
        return string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(ConfigDirectory, "games.json")
            : overridePath;
    }

    /// <summary>按平台解析数据根目录；Linux 优先 XDG_DATA_HOME（须为绝对路径才生效）。</summary>
    private static string ResolveDataHomeRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdgDataHome) || !Path.IsPathRooted(xdgDataHome)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdgDataHome!; // IsPathRooted 已保证非空
    }
}
