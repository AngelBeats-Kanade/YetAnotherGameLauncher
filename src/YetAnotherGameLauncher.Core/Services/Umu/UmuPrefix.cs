using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Services.Umu;

/// <summary>
/// Proton 兼容 WINE prefix 布局（移植上游 umu setup_pfx）。
/// WINEPREFIX 目录内：pfx→. 符号链接、shadercache/、gstreamer-1.0/、tracked_files，
/// 以及 drive_c/users/{steamuser,当前用户} 互链。文件锁用 FileStream 独占，禁止 unsafe。
/// </summary>
public static class UmuPrefix
{
    /// <summary>
    /// 准备 Proton 兼容 prefix。幂等：已正确就位的结构不会被破坏。
    /// </summary>
    /// <param name="prefixRoot">WINEPREFIX 绝对路径（如 ~/.local/share/yagl/prefixes/gameId）。</param>
    /// <param name="unixUserName">当前 Unix 用户名（Linux）；null 时尝试从环境 USER 推断。</param>
    public static void Setup(string prefixRoot, string? unixUserName = null)
    {
        if (string.IsNullOrWhiteSpace(prefixRoot))
        {
            throw new ArgumentException("WINEPREFIX 路径为空。", nameof(prefixRoot));
        }

        var root = Path.GetFullPath(prefixRoot);
        // root 自身是悬空符号链接（旧布局目标被删/卷未挂载）时 CreateDirectory 报 EEXIST
        // → 启动永久失败（F21）；悬空链接先清再建，真实目录/可用链接不受影响
        DeleteDanglingLink(root);
        Directory.CreateDirectory(root);

        using var _ = AcquireLock(Path.Combine(root, "pfx.lock"));

        var pfx = Path.Combine(root, "pfx");
        EnsurePfxSymlink(root, pfx);

        Directory.CreateDirectory(Path.Combine(root, "shadercache"));
        Directory.CreateDirectory(Path.Combine(root, "gstreamer-1.0"));

        var tracked = Path.Combine(root, "tracked_files");
        if (!File.Exists(tracked))
        {
            File.WriteAllText(tracked, string.Empty);
        }

        SetupUserLinks(pfx, unixUserName ?? Environment.GetEnvironmentVariable("USER") ?? "steamuser");
    }

    /// <summary>在 Linux 上对路径加用户执行位；Windows 为 no-op。IO 失败原样抛出，调用方负责映射为 LaunchException。</summary>
    public static void EnsureUserExecute(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
    }

    /// <summary>独占创建锁文件并持有到 Dispose（FileStream 独占，不使用 flock 系统调用）。</summary>
    public static IDisposable AcquireLock(string lockPath, int timeoutMilliseconds = 30_000)
    {
        var dir = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw new UpdateException(
                        $"获取 umu 组件锁超时：{lockPath}。可能有其它安装任务正在进行，请稍后重试。");
                }

                Thread.Sleep(100);
            }
        }
    }

    private static void EnsurePfxSymlink(string root, string pfx)
    {
        if (Directory.Exists(pfx) && !IsSymlink(pfx))
        {
            // 已是真实目录（旧版或用户自建）：保留
            return;
        }

        if (IsSymlink(pfx))
        {
            var target = ResolveSymlinkTarget(pfx, root);
            if (string.Equals(target, ".", StringComparison.Ordinal) ||
                string.Equals(target, root, StringComparison.Ordinal))
            {
                return;
            }

            DeleteLinkOrDirectory(pfx);
        }

        if (File.Exists(pfx))
        {
            File.Delete(pfx);
        }

        try
        {
            Directory.CreateSymbolicLink(pfx, ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 无 symlink 权限时退化为真实目录（Proton 仍可用，只是占用略大）
            Directory.CreateDirectory(pfx);
        }
    }

    private static void SetupUserLinks(string pfx, string unixUserName)
    {
        var users = Path.Combine(pfx, "drive_c", "users");
        if (!Directory.Exists(Path.Combine(pfx, "drive_c")))
        {
            // prefix 尚未 wineboot：由 Proton 首次创建；仅在存在 drive_c 时补用户链
            return;
        }

        // users/steamuser 自身是悬空符号链接时裸 mkdir 报 EEXIST → IOException 穿出 Setup
        // → 启动永久失败（F21 第 13 轮补充）：悬空链接先清再建（与 EnsurePfxSymlink 自愈同型）
        DeleteDanglingLink(users);
        Directory.CreateDirectory(users);

        var steamuser = Path.Combine(users, "steamuser");
        var wineuser = Path.Combine(users, unixUserName);

        DeleteDanglingLink(steamuser);

        if (!ExistsLinkOrDir(wineuser) && !ExistsLinkOrDir(steamuser))
        {
            Directory.CreateDirectory(steamuser);
            TryCreateDirectoryLink(wineuser, "steamuser");
        }
        else if (Directory.Exists(wineuser) && !IsSymlink(wineuser) && !ExistsLinkOrDir(steamuser))
        {
            TryCreateDirectoryLink(steamuser, unixUserName);
        }
        else if (!ExistsLinkOrDir(wineuser) && Directory.Exists(steamuser) && !IsSymlink(steamuser))
        {
            TryCreateDirectoryLink(wineuser, "steamuser");
        }
    }

    /// <summary>清除悬空符号链接（链接本身在、目标不存在）：真实目录/可用链接原样保留。
    /// 判定必须以"链接目标的可达性"为准——.NET 10 实测（/tmp 探针）对 Unix 悬空目录链接
    /// File.Exists 返回 true、Directory.Exists 返回 false，且 Directory.Delete 抛
    /// DirectoryNotFoundException，Unix 侧移除链接本身须用 File.Delete（unlink 语义）。
    /// Windows 语义相反：目录链接是 directory reparse point，File.Delete（DeleteFileW）对它报
    /// ERROR_ACCESS_DENIED → UnauthorizedAccessException（2026-09-25 Windows CI 三用例实锤），
    /// 须用 Directory.Delete（RemoveDirectory 只摘 reparse point 本身、不跟随目标，悬空安全）。</summary>
    private static void DeleteDanglingLink(string path)
    {
        if (!IsSymlink(path))
        {
            return;
        }

        var info = new DirectoryInfo(path);
        var target = info.LinkTarget;
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        var resolved = Path.IsPathRooted(target)
            ? target
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, target));
        if (!Directory.Exists(resolved) && !File.Exists(resolved))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    private static bool ExistsLinkOrDir(string path) =>
        Directory.Exists(path) || IsSymlink(path);

    private static bool IsSymlink(string path)
    {
        try
        {
            // 判定基准 = "是否为链接"（官方 LinkTarget 语义），不得要求 info.Exists——
            // DirectoryInfo.Exists 跟随链接，悬空链接会误判 false，让 EnsurePfxSymlink 的
            // 错误链接自愈分支全部跳过 → CreateSymbolicLink/CreateDirectory 连环 EEXIST
            // → 启动永久失败（F21）
            var info = new DirectoryInfo(path);
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ResolveSymlinkTarget(string path, string root)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var target = info.LinkTarget;
            if (string.IsNullOrEmpty(target))
            {
                return target;
            }

            return Path.IsPathRooted(target)
                ? Path.GetFullPath(target)
                : Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, target)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryCreateDirectoryLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 忽略：用户链接缺失时 Proton 会自行创建 steamuser
        }
    }

    private static void DeleteLinkOrDirectory(string path)
    {
        if (IsSymlink(path))
        {
            try
            {
                Directory.Delete(path);
            }
            catch (DirectoryNotFoundException)
            {
                // 悬空目录链接（仅 Unix 可达；Windows 的 Directory.Delete 直接成功）：
                // Directory.Exists=false → Directory.Delete 抛 DNFE（.NET 10 /tmp 探针实测）
                // ——链接本身用 unlink 移除
                File.Delete(path);
            }

            return;
        }

        if (Directory.Exists(path))
        {
            // 目录树删除走统一防线：占用/只读时尽力删，不让单个文件打断整个前缀清理
            FileUtilities.TryDeleteDirectory(path);
        }
    }
}
