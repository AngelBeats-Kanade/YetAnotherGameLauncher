namespace YetAnotherGameLauncher.Core.Utilities;

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
        File.Move(tempPath, path, overwrite: true);
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

    /// <summary>gameId 只保留文件名安全字符，其余替换为 '-'；清洗后为空返回 "game"（启动日志等文件名用）。</summary>
    public static string SanitizeGameId(string gameId)
    {
        var sanitized = new string(gameId.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return sanitized.Length == 0 ? "game" : sanitized;
    }

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
