using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>清单中单个文件的检查结论。</summary>
public enum FileStatus
{
    Ok,
    Missing,
    SizeMismatch,
    Md5Mismatch,
}

/// <summary>单个文件的检查结果。</summary>
public sealed record FileCheckResult(string Path, FileStatus Status);

/// <summary>整个清单的检查结果。</summary>
public sealed record ManifestVerificationResult(IReadOnlyList<FileCheckResult> Results)
{
    public bool IsComplete => Results.All(r => r.Status == FileStatus.Ok);

    /// <summary>需要（重新）下载的文件：缺失、大小不符或内容损坏。</summary>
    public IReadOnlyList<FileCheckResult> NeedsDownload =>
        [.. Results.Where(r => r.Status != FileStatus.Ok)];
}

/// <summary>
/// 对照清单检查本地安装目录。VerifyFast 只比存在性与大小（快），
/// VerifyFull 额外计算 MD5（慢，用于事后校验与修复）。
/// </summary>
public static class ManifestVerifier
{
    /// <summary>快速校验：只比存在性与文件大小，不读内容。</summary>
    /// <param name="installDir">安装目录。</param>
    /// <param name="manifest">游戏清单。</param>
    /// <param name="onFileChecked">逐文件回调（已检查数, 总数）——大库扫描时驱动进度条，可选。</param>
    public static ManifestVerificationResult VerifyFast(
        string installDir, GameManifest manifest, Action<int, int>? onFileChecked = null) =>
        Verify(installDir, manifest, withMd5: false, onFileChecked);

    /// <summary>全量校验：在快速校验之上额外计算并比对 MD5，用于下载后确认完整性。</summary>
    /// <param name="installDir">安装目录。</param>
    /// <param name="manifest">游戏清单。</param>
    /// <param name="onFileChecked">逐文件回调（已检查数, 总数）——大库 MD5 耗时数分钟，必须给进度，可选。</param>
    public static ManifestVerificationResult VerifyFull(
        string installDir, GameManifest manifest, Action<int, int>? onFileChecked = null) =>
        Verify(installDir, manifest, withMd5: true, onFileChecked);

    /// <summary>检查单个文件：存在性 → 大小 →（可选）MD5，返回首个不匹配项或 Ok。</summary>
    public static FileStatus CheckFile(string fullPath, ManifestFile file, bool withMd5)
    {
        if (!File.Exists(fullPath))
        {
            return FileStatus.Missing;
        }

        var length = new FileInfo(fullPath).Length;
        if (length != file.Size)
        {
            return FileStatus.SizeMismatch;
        }

        if (withMd5 && !string.Equals(
                Utilities.Hashing.Md5Hex(fullPath), file.Md5, StringComparison.OrdinalIgnoreCase))
        {
            return FileStatus.Md5Mismatch;
        }

        return FileStatus.Ok;
    }

    private static ManifestVerificationResult Verify(
        string installDir, GameManifest manifest, bool withMd5, Action<int, int>? onFileChecked)
    {
        var results = new List<FileCheckResult>(manifest.Files.Count);
        foreach (var file in manifest.Files)
        {
            var fullPath = ResolveSafe(installDir, file.Path);
            results.Add(new FileCheckResult(file.Path, CheckFile(fullPath, file, withMd5)));
            onFileChecked?.Invoke(results.Count, manifest.Files.Count);
        }

        return new ManifestVerificationResult(results);
    }

    /// <summary>把清单中的相对路径安全地解析到安装目录内，防止清单被篡改后越界读写。</summary>
    public static string ResolveSafe(string installDir, string manifestPath)
    {
        var root = Path.GetFullPath(installDir);
        var normalized = manifestPath.Replace('\\', '/');
        var candidate = Path.GetFullPath(Path.Combine(root, normalized));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)
            && !string.Equals(candidate, root.TrimEnd(Path.DirectorySeparatorChar), comparison))
        {
            throw new InvalidOperationException($"Manifest path escapes sandbox; rejected: {manifestPath}");
        }

        return candidate;
    }
}
