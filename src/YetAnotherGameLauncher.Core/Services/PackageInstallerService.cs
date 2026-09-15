using System.IO.Compression;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// 包式渠道安装器（终末地等整包分发的游戏）：
/// 下载压缩包 → size/MD5 校验 → 解压进安装目录 → 清理。
/// 支持两段式：Predownload 暂存压缩包，Apply 解压落盘。
/// </summary>
public sealed class PackageInstallerService(IDownloader downloader, ILogger? logger = null)
{

    /// <summary>两段式预下载的压缩包暂存目录（位于 .yagl/predownload/packages，Apply 时就地解压）。</summary>
    public static string PackagesDir(string installDir) => Path.Combine(
        installDir, LocalStateService.StateDirName, IncrementalUpdateService.PredownloadDirName, "packages");

    /// <summary>下载并解压全部压缩包（完整安装/更新）。</summary>
    public async Task InstallAsync(
        string installDir,
        GameManifest packageManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var packagesDir = Path.Combine(installDir, LocalStateService.StateDirName, "packages");
        var staged = await DownloadPackagesAsync(packagesDir, packageManifest, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new UpdateProgress(UpdatePhase.Patching, 0, 0, packageManifest.Files.Count, packageManifest.Files.Count, null));
        foreach (var (manifestFile, archivePath) in staged)
        {
            ExtractArchive(archivePath, installDir, manifestFile.Path);
            // 刚写完的 zip 可能正被杀软扫描锁定（Windows）：删不掉就留给下方目录清理重试，别打断安装
            FileUtilities.DeleteQuiet(archivePath);
        }

        // Windows 上残留包被占用时整树删除会抛异常，尽力清理即可
        FileUtilities.TryDeleteDirectory(packagesDir, logger);
        logger?.LogInformation("Package install finished: {Version} ({Count} archives)", packageManifest.Version, packageManifest.Files.Count);
        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, packageManifest.Files.Count, packageManifest.Files.Count, null));
    }

    /// <summary>预下载：把压缩包下载到暂存目录并写入暂存清单。</summary>
    public async Task PredownloadAsync(
        string installDir,
        GameManifest packageManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var staging = IncrementalUpdateService.ResetStaging(installDir);
        await DownloadPackagesAsync(PackagesDir(installDir), packageManifest, progress, cancellationToken).ConfigureAwait(false);

        await IncrementalUpdateService.WriteStagedManifestAsync(staging, packageManifest, cancellationToken).ConfigureAwait(false);

        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, packageManifest.Files.Count, packageManifest.Files.Count, null));
    }

    /// <summary>
    /// 应用预下载：直接解压暂存压缩包（Predownload 的产物），完成后清理暂存目录。
    /// 正常情况下零下载；仅当某个包缺失或校验不通过（如暂存被中断/篡改）时才回退重新下载该包。
    /// </summary>
    public async Task ApplyPredownloadAsync(
        string installDir,
        GameManifest packageManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var staging = IncrementalUpdateService.PredownloadDir(installDir);
        var stagedDir = PackagesDir(installDir);
        var total = packageManifest.Files.Count;

        progress?.Report(new UpdateProgress(UpdatePhase.Patching, 0, 0, 0, total, null));
        foreach (var (file, index) in packageManifest.Files.Select((f, i) => (f, i)))
        {
            ManifestChecks.EnsureDownloadUrl(file.Url, "Package entry", file.Path);

            var archivePath = StagedArchivePath(stagedDir, file);
            if (!IsArchiveIntact(archivePath, file))
            {
                logger?.LogInformation("Staged package missing or corrupt, re-downloading: {Path}", file.Path);
                await downloader.DownloadFileAsync(
                    new DownloadRequest(file.Url, archivePath, file.Size, file.Md5), null, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new UpdateProgress(UpdatePhase.Patching, 0, 0, index, total, file.Path));
            ExtractArchive(archivePath, installDir, file.Path);
        }

        if (Directory.Exists(staging))
        {
            FileUtilities.TryDeleteDirectory(staging, logger);
        }

        logger?.LogInformation("Predownload applied: {Version} ({Count} archives)", packageManifest.Version, total);
        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, total, total, null));
    }

    /// <summary>压缩包在包目录内的落盘路径（清单里的反斜杠路径取末段文件名）。</summary>
    private static string StagedArchivePath(string packagesDir, ManifestFile file) =>
        Path.Combine(packagesDir, Path.GetFileName(file.Path.Replace('\\', '/')));

    /// <summary>暂存包完整性：文件存在、大小与 MD5 均与清单一致。</summary>
    private static bool IsArchiveIntact(string archivePath, ManifestFile file) =>
        File.Exists(archivePath)
        && new FileInfo(archivePath).Length == file.Size
        && Hashing.Md5Hex(archivePath).Equals(file.Md5, StringComparison.OrdinalIgnoreCase);

    private async Task<List<(ManifestFile File, string ArchivePath)>> DownloadPackagesAsync(
        string packagesDir,
        GameManifest packageManifest,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(packagesDir);
        var totalBytes = packageManifest.Files.Sum(f => f.Size);
        var downloaded = 0L;
        var staged = new List<(ManifestFile, string)>();

        foreach (var (file, index) in packageManifest.Files.Select((f, i) => (f, i)))
        {
            ManifestChecks.EnsureDownloadUrl(file.Url, "Package entry", file.Path);

            progress?.Report(new UpdateProgress(UpdatePhase.Downloading, totalBytes, downloaded, index, packageManifest.Files.Count, file.Path));

            var archivePath = StagedArchivePath(packagesDir, file);
            await downloader.DownloadFileAsync(
                new DownloadRequest(file.Url, archivePath, file.Size, file.Md5), null, cancellationToken).ConfigureAwait(false);

            downloaded += file.Size;
            staged.Add((file, archivePath));
        }

        return staged;
    }

    private static void ExtractArchive(string archivePath, string installDir, string displayName)
    {
        try
        {
            // 不用 ZipFile.ExtractToDirectory：它在 Unix 上把含 '\' 的条目名当字面文件名
            // （dotnet/runtime#98247），Windows 打包器产出的包会在 Linux 解成安装根目录下的
            // 一堆平铺垃圾文件。手动遍历统一归一 '/'，顺带做沙箱校验与只读属性处理。
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                var target = ResolveEntryTarget(installDir, entry.FullName);
                if (target is null)
                {
                    var dirName = entry.FullName.Replace('\\', '/').TrimEnd('/');
                    if (dirName.Length > 0)
                    {
                        Directory.CreateDirectory(Path.Combine(installDir, dirName));
                    }

                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    // 只读属性会让 overwrite 失败（Windows），就地解除
                    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                }

                entry.ExtractToFile(target, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"Failed to extract {displayName}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 解压条目落点：归一 '\'→'/'、去首部 '/'，拒绝 ".." 段与盘符根（清单不可信，防穿越）；
    /// 目录条目（含空名）返回 null 并由调用方建目录，普通条目返回安装目录内的绝对路径。
    /// </summary>
    private static string? ResolveEntryTarget(string installDir, string entryName)
    {
        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.EndsWith('/'))
        {
            return null;
        }

        var parts = normalized.Split('/');
        if (parts.Contains("..", StringComparer.Ordinal) || Path.IsPathRooted(normalized))
        {
            throw new IOException($"Archive entry escapes sandbox: {entryName}");
        }

        return Path.Combine([installDir, .. parts]);
    }
}
