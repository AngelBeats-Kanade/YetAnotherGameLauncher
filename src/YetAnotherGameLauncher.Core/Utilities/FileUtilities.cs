namespace YetAnotherGameLauncher.Core.Utilities;

using Microsoft.Extensions.Logging;

/// <summary>文件工具：原子写入、静默删除、文件名清洗与可执行位检查。</summary>
public static class FileUtilities
{
    /// <summary>原子写入文本：先写同目录 .tmp 再 Move 覆盖，避免写入中途崩溃损坏目标文件。</summary>
    public static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);

        // Windows 语义坑：目标被占用（编辑器/杀软）或带只读属性时 Move 覆盖会抛
        // IOException/UnauthorizedAccessException，而 Linux 的 rename() 总能成功。
        // 只读是常见原因，就地解除后重试一次；仍失败则把真实原因包进可操作的错误里。
        try
        {
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(path))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            try
            {
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception retry) when (retry is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"无法写入 {path}，文件可能正被其它程序占用：{retry.Message}", retry);
            }
        }
    }

    /// <summary>尽力删除文件：占用/权限等失败静默忽略，仅用于清理场景。</summary>
    public static void DeleteQuiet(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>路径是否为符号链接/junction 等重解析点；探测失败按否处理（后续删除自会再兜）。</summary>
    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>只删除链接本身而不进目标子树：先按目录链接删，失败再按文件链接删。</summary>
    private static bool TryDeleteLinkQuiet(string path, ILogger? logger)
    {
        try
        {
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogDebug(ex, "Skipped reparse point during tree delete: {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// 尽力删除目录树：逐条目删除后自底向上删子目录，返回目录是否已完全删除。
    /// Windows 上单个被占用/只读的文件会让 <see cref="Directory.Delete(string,bool)"/> 整体抛异常，
    /// 进而打断更新/清理链（Linux 上打开中的文件照样可删，问题只在 Windows 暴露）；
    /// 这里能删多少删多少，删不掉的残留留给下次重试或事后修复。
    /// </summary>
    public static bool TryDeleteDirectory(string path, ILogger? logger = null)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        var clean = true;
        try
        {
            // IgnoreInaccessible：子树含无 ListDirectory 权限的目录时跳过该子项（官方语义），
            // 其余照删——单不可读子目录不得打断整链（F25；path 自身不可读时枚举仍抛，
            // 由外层 catch 兜成 false 保住 best-effort 契约）。
            // AttributesToSkip 必须显式归零：默认 Hidden|System 会在 Linux 上把点前缀条目
            // （.installed.ok/.yagl-* 等，.NET 标记为 Hidden）从枚举剔除——删不净即回归
            //（复审 R1 探针实锤：默认 1 条 vs 完整 2 条）
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                path, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.None,
                }))
            {
                // 符号链接/junction 只删链接本身：递归会穿过链接把目标处（可能远在自管目录之外）
                // 的真实文件删掉。先按目录链接删，失败（实为文件链接）再按文件删。
                if (IsReparsePoint(entry))
                {
                    if (!TryDeleteLinkQuiet(entry, logger))
                    {
                        clean = false;
                    }

                    continue;
                }

                if (Directory.Exists(entry) && !File.Exists(entry))
                {
                    clean &= TryDeleteDirectory(entry, logger);
                }
                else
                {
                    DeleteQuiet(entry);
                    if (File.Exists(entry))
                    {
                        logger?.LogDebug("Skipped in-use file during tree delete: {Path}", entry);
                        clean = false;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogDebug(ex, "Cannot enumerate directory tree, left in place: {Path}", path);
            return false;
        }

        try
        {
            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogDebug(ex, "Directory still in use, left in place: {Path}", path);
            return false;
        }

        return clean;
    }

    /// <summary>gameId 只保留文件名安全字符，其余替换为 '-'；清洗后为空返回 "game"（启动日志等文件名用）。</summary>
    public static string SanitizeGameId(string gameId)
    {
        var sanitized = new string(gameId.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return sanitized.Length == 0 ? "game" : sanitized;
    }

    /// <summary>
    /// 启动日志文件路径：{logDir}/launch-{gameId}-{yyyyMMdd-HHmmss}-{4位随机}.log。
    /// 时间戳精确到秒，同秒内对同一游戏的两次启动会互相覆盖日志，追加短随机后缀保证唯一。
    /// GameLauncherService 与 NativeUmuLauncher 共用，保持日志命名单一来源。
    /// </summary>
    public static string LaunchLogFilePath(string logDirectory, string gameId) => Path.Combine(
        logDirectory,
        $"launch-{SanitizeGameId(gameId)}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}.log");

    /// <summary>文件是否存在且可执行：Linux 校验 UserExecute 位；Windows 无执行位概念，存在即可。</summary>
    public static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
