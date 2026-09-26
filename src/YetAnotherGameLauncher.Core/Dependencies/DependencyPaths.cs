namespace YetAnotherGameLauncher.Core.Dependencies;

/// <summary>
/// 依赖系统的磁盘布局。下载缓存全局共享（多个 prefix 装同一依赖只下载一次）；
/// 安装状态挂在各 prefix 根下（随 prefix 删除自动重置）。
/// </summary>
public static class DependencyPaths
{
    /// <summary>解压暂存目录名（位于缓存根下，按依赖 id 分目录）。</summary>
    public const string StagingDirName = "staging";

    /// <summary>prefix 根内的安装状态目录名。</summary>
    public const string StateDirName = ".yagl-deps";

    /// <summary>指定依赖的完成标记文件名。</summary>
    public static string MarkerFileName(string dependencyId) => $"{dependencyId}.ok";

    /// <summary>指定依赖的完成标记完整路径。</summary>
    public static string MarkerPath(string stateDirectory, string dependencyId) =>
        Path.Combine(stateDirectory, MarkerFileName(dependencyId));

    /// <summary>下载缓存根：~/.cache/yagl/deps（cacheHome 缺省每调用实时读，XDG_CACHE_HOME 语义与 UmuPaths 一致）。</summary>
    public static string CacheRoot(string? cacheHome = null) =>
        Path.Combine(cacheHome ?? DefaultCacheHome(), "yagl", "deps");

    /// <summary>解压暂存目录：{cacheRoot}/staging/&lt;dependencyId&gt;。</summary>
    public static string StagingDirectory(string cacheRoot, string dependencyId) =>
        Path.Combine(cacheRoot, StagingDirName, dependencyId);

    /// <summary>缓存根缺省：Linux 尊重 XDG_CACHE_HOME，否则 ~/.cache；Windows 回退 {DataHome}/cache。</summary>
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
