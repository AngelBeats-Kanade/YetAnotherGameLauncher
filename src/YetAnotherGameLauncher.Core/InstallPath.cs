namespace YetAnotherGameLauncher.Core;

/// <summary>安装路径解析：支持 "~" 展开、绝对/相对安装目录。</summary>
public static class InstallPath
{
    /// <summary>解析游戏安装目录：installDir 为绝对路径时直接使用，否则相对 installRoot。</summary>
    public static string Resolve(string installRoot, string installDir)
    {
        var dir = ExpandUserPath(installDir);
        if (Path.IsPathRooted(dir))
        {
            return Path.GetFullPath(dir);
        }

        return Path.GetFullPath(Path.Combine(ExpandUserPath(installRoot), dir));
    }

    /// <summary>把开头的 "~" 展开为用户主目录。</summary>
    public static string ExpandUserPath(string path)
    {
        if (path == "~")
        {
            return Home;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(Home, path[2..]);
        }

        return path;
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
