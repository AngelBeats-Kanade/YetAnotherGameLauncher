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
    private readonly IDownloader _downloader = downloader;

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
        var staged = await DownloadPackagesAsync(packagesDir, packageManifest, progress, cancellationToken);

        progress?.Report(new UpdateProgress(UpdatePhase.Patching, 0, 0, packageManifest.Files.Count, packageManifest.Files.Count, null));
        foreach (var (manifestFile, archivePath) in staged)
        {
            ExtractArchive(archivePath, installDir, manifestFile.Path);
            File.Delete(archivePath);
        }

        Directory.Delete(packagesDir, recursive: true);
        logger?.LogInformation("包式安装完成：{Version}（{Count} 个压缩包）", packageManifest.Version, packageManifest.Files.Count);
        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, packageManifest.Files.Count, packageManifest.Files.Count, null));
    }

    /// <summary>预下载：把压缩包下载到暂存目录并写入暂存清单。</summary>
    public async Task PredownloadAsync(
        string installDir,
        GameManifest packageManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var staging = IncrementalUpdateService.PredownloadDir(installDir);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        await DownloadPackagesAsync(PackagesDir(installDir), packageManifest, progress, cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(staging, "manifest.json"),
            JsonSerializer.Serialize(packageManifest, Json.Default),
            cancellationToken);

        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, packageManifest.Files.Count, packageManifest.Files.Count, null));
    }

    /// <summary>应用预下载：解压暂存压缩包并清理暂存目录。</summary>
    public async Task ApplyPredownloadAsync(
        string installDir,
        GameManifest packageManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InstallAsync(installDir, packageManifest, progress, cancellationToken);

        var staging = IncrementalUpdateService.PredownloadDir(installDir);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }
    }

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
            if (file.Url is null)
            {
                throw new UpdateException($"压缩包条目缺少下载地址：{file.Path}");
            }

            progress?.Report(new UpdateProgress(UpdatePhase.Downloading, totalBytes, downloaded, index, packageManifest.Files.Count, file.Path));

            var archivePath = Path.Combine(packagesDir, Path.GetFileName(file.Path.Replace('\\', '/')));
            await _downloader.DownloadFileAsync(
                new DownloadRequest(file.Url, archivePath, file.Size, file.Md5), null, cancellationToken);

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
            throw new UpdateException($"解压 {displayName} 失败：{ex.Message}", ex);
        }
    }
}
