namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// 库洛官方启动器背景探测：官方启动器会把当前背景动画的帧序列缓存在
/// 游戏目录旁的 kr_game_cache/animate_bg/&lt;hash&gt;/home_N.jpg（N 为帧序号）。
/// 复用这些帧即可让启动器背景与官方启动器保持一致（随版本更新）。
/// </summary>
public static class KuroLauncherBackground
{
    /// <summary>
    /// 定位最新的背景帧。优先在游戏安装目录的兄弟目录（kr_game_cache）中查找，
    /// 再扫描各盘符的常见安装位置；返回动画末帧（文件序号最大），找不到返回 null。
    /// </summary>
    public static string? FindLatestFrame(string? gameInstallDir = null)
    {
        var latest = CandidateRoots(gameInstallDir)
            .Where(Directory.Exists)
            .SelectMany(EnumerateFrames)
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return latest;
    }

    private static IEnumerable<string> CandidateRoots(string? gameInstallDir)
    {
        // 游戏安装目录上溯：…\Wuthering Waves\Wuthering Waves Game → …\Wuthering Waves\kr_game_cache
        if (!string.IsNullOrWhiteSpace(gameInstallDir))
        {
            var dir = Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameInstallDir)));
            for (var i = 0; i < 3 && dir is not null; i++)
            {
                yield return Path.Combine(dir, "kr_game_cache");
                dir = Path.GetDirectoryName(dir);
            }
        }

        foreach (var drive in DriveInfo.GetDrives().Select(d => d.Name))
        {
            yield return Path.Combine(drive, "Wuthering Waves", "kr_game_cache");
        }
    }

    private static IEnumerable<string> EnumerateFrames(string root)
    {
        try
        {
            return Directory.EnumerateFiles(
                Path.Combine(root, "animate_bg"), "home_*.jpg", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
