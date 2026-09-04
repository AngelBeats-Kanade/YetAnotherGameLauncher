namespace YetAnotherGameLauncher.Core;

/// <summary>应用数据目录等平台相关路径。</summary>
public static class AppPaths
{
    /// <summary>配置目录：Windows 为 %APPDATA%\yagl，Linux 为 ~/.config/yagl。</summary>
    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yagl");

    public static string ConfigFilePath { get; } = Path.Combine(ConfigDirectory, "games.json");
}
