using System.IO.Compression;
using System.Text.Json;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;
using Microsoft.Extensions.Logging;

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
            File.Delete(archivePath);
        }

        Directory.Delete(packagesDir, recursive: true);
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
            Directory.Delete(staging, recursive: true);
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
            ZipFile.ExtractToDirectory(archivePath, installDir, overwriteFiles: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new UpdateException($"Failed to extract {displayName}: {ex.Message}", ex);
        }
    }
}
