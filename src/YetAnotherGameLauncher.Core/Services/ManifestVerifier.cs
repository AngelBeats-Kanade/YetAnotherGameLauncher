using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>清单中单个文件的检查结论。</summary>
public enum FileStatus
{
    Ok,
    Missing,
    SizeMismatch,
    Md5Mismatch,

    /// <summary>存在但被拒读（chmod 000/ACL）：≠缺失（报 Missing 会触发重下、重下同样拒读的循环面），
    /// 并入 NeedsDownload 走一轮补下载，复验仍不可读时由 SyncAsync 以结构化报错收尾（F44）。</summary>
    Unreadable,
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

    /// <summary>F18 确定性测试缝（生产恒 null）：Exists 通过后、实际探测前触发——
    /// 在测试里注入"检查与操作之间文件被外部移除"的 TOCTOU 窗口（官方 File.Exists Remarks
    /// 明示该窗口存在）。覆盖 Length 与 Md5 两段（整体 try 包裹，修复范围含第 13 轮补充的 MD5 段）。</summary>
    internal static Action? FileRemovedBetweenCheckAndProbeForTests;

    /// <summary>检查单个文件：存在性 → 大小 →（可选）MD5，返回首个不匹配项或 Ok。
    /// 清单 size ≤ 0 或 MD5 为空表示渠道未提供该字段的校验信息：跳过对应项而非按目标值比较——
    /// 否则字段缺失的健康文件恒判损坏，下载器重下真内容后仍过不了全量校验、更新必然失败
    /// （与 <see cref="HttpFileDownloader.Verify"/> 同一语义，2026-09-20 复审补齐）。
    /// 探测段整体包 <see cref="FileNotFoundException"/>：Exists 与 Length/Md5 之间文件被外部
    /// 移除（手删/云同步隔离/杀毒）时按 Missing 报告走补下载，而不是让整轮校验以裸异常中止
    /// （F18——只包 Length 段会漏 MD5 段的 Hashing.Md5Hex 内部 File.OpenRead）。
    /// 拒读（chmod 000/ACL）另报 <see cref="FileStatus.Unreadable"/>（F44）：缺失与拒读语义
    /// 分开，均不中止整轮校验。</summary>
    public static FileStatus CheckFile(string fullPath, ManifestFile file, bool withMd5)
    {
        if (!File.Exists(fullPath))
        {
            return FileStatus.Missing;
        }

        try
        {
            FileRemovedBetweenCheckAndProbeForTests?.Invoke();

            if (file.Size > 0 && new FileInfo(fullPath).Length != file.Size)
            {
                return FileStatus.SizeMismatch;
            }

            if (withMd5 && !string.IsNullOrEmpty(file.Md5) && !string.Equals(
                    Utilities.Hashing.Md5Hex(fullPath), file.Md5, StringComparison.OrdinalIgnoreCase))
            {
                return FileStatus.Md5Mismatch;
            }

            return FileStatus.Ok;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // FNFE = 文件被移除（FileInfo.Length）；DNFE = 父目录被移除（Hashing.Md5Hex 的
            // File.OpenRead 对父目录缺失抛 DNFE）——同一 TOCTOU 族，均按 Missing 走补下载
            return FileStatus.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            // F44：拒读（chmod 000/ACL）≠ 缺失——报 Missing 会触发重下、重下同样被拒的循环面。
            // 独立 Unreadable 并入 NeedsDownload：一轮补下载（写盘 UAE 由下载器的本地错误臂
            // 分类），复验仍不可读时 SyncAsync 以 "path(Unreadable)" 结构化报错收尾，轮次有界
            return FileStatus.Unreadable;
        }
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
