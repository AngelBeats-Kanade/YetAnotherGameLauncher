namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>文件写入工具。</summary>
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
}
