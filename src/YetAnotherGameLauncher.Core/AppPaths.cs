namespace YetAnotherGameLauncher.Core;

/// <summary>应用数据目录等平台相关路径。</summary>
public static class AppPaths
{
    /// <summary>配置目录：Windows 为 %APPDATA%\yagl，Linux 为 ~/.config/yagl。</summary>
    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yagl");

    /// <summary>配置文件路径。环境变量 YAGL_CONFIG 可覆盖（测试/多实例场景）。</summary>
    public static string GetConfigFilePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("YAGL_CONFIG");
        return string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(ConfigDirectory, "games.json")
            : overridePath;
    }
}
