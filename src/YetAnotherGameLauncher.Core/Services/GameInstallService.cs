using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>全量同步选项。</summary>
public sealed class GameInstallServiceOptions
{
    /// <summary>文件级下载并发数。</summary>
    public int MaxParallelFiles { get; set; } = 8;
}

/// <summary>
/// 全量同步：对照清单检查安装目录 → 并行补齐缺失/损坏文件 → MD5 事后校验 → 清理游离文件。
/// 参考鸣潮官方启动器 sync_files + verify_new_version 流程。
/// </summary>
public sealed class GameInstallService(
    IDownloader downloader,
    GameInstallServiceOptions? options = null,
    ILogger? logger = null)
{
    private readonly GameInstallServiceOptions _options = options ?? new();

    /// <summary>不在清单内也要保留的顶层目录与文件（存档、启动器自身数据、官方启动器兼容文件）。</summary>
    private static readonly IReadOnlyList<string> PreservedEntries = [".yagl", "Saved", "launcherDownloadConfig.json"];

    /// <summary>对照清单把安装目录补齐到目标版本：快速校验 → 只并行下载缺失/损坏文件 → 全量 MD5 校验 → 清理游离文件。</summary>
    public async Task SyncAsync(
        string installDir,
        GameManifest manifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Report(progress, UpdatePhase.Checking, 0, 0, 0, manifest.Files.Count, null);

        var verification = ManifestVerifier.VerifyFast(installDir, manifest);
        var needed = verification.NeedsDownload
            .Join(manifest.Files, n => n.Path, f => f.Path, (_, f) => f)
            .ToList();
        var totalBytes = needed.Sum(f => f.Size);

        logger?.LogInformation("Sync {Version}: {Needed} of {Total} files to download",
            manifest.Version, needed.Count, manifest.Files.Count);

        long downloadedBytes = 0;
        var filesDone = 0;
        var gate = new object();
        Report(progress, UpdatePhase.Downloading, totalBytes, 0, 0, needed.Count, null);

        await Parallel.ForEachAsync(
            needed,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxParallelFiles,
                CancellationToken = cancellationToken,
            },
            async (file, token) =>
            {
                if (file.Url is null)
                {
                    throw new UpdateException($"Manifest entry has no download URL: {file.Path}");
                }

                var destination = ManifestVerifier.ResolveSafe(installDir, file.Path);

                // 并行下载共享计数：per-file 字节回调只在 lock 内读累计值，
                // 文件完成时才累加并上报（避免每字节回调高频打散 UI 进度条）
                var perFile = new Progress<long>(bytes =>
                {
                    lock (gate)
                    {
                        Report(progress, UpdatePhase.Downloading, totalBytes, downloadedBytes, filesDone, needed.Count, file.Path);
                    }
                });

                await downloader.DownloadFileAsync(
                    new DownloadRequest(file.Url, destination, file.Size, file.Md5),
                    perFile,
                    token).ConfigureAwait(false);

                lock (gate)
                {
                    downloadedBytes += file.Size;
                    filesDone++;
                }

                Report(progress, UpdatePhase.Downloading, totalBytes, downloadedBytes, filesDone, needed.Count, file.Path);
            }).ConfigureAwait(false);

        Report(progress, UpdatePhase.Verifying, totalBytes, downloadedBytes, needed.Count, needed.Count, null);

        var post = ManifestVerifier.VerifyFull(installDir, manifest);
        if (!post.IsComplete)
        {
            var broken = string.Join("、", post.NeedsDownload.Select(n => $"{n.Path}({n.Status})"));
            throw new UpdateException($"Post-download verification failed. Broken files: {broken}");
        }

        Report(progress, UpdatePhase.CleaningUp, totalBytes, downloadedBytes, needed.Count, needed.Count, null);
        CleanupStaleFiles(installDir, manifest);

        Report(progress, UpdatePhase.Done, totalBytes, downloadedBytes, needed.Count, needed.Count, null);
    }

    /// <summary>删除清单之外的游离文件（跳过存档与启动器数据目录）。</summary>
    private static void CleanupStaleFiles(string installDir, GameManifest manifest)
    {
        if (!Directory.Exists(installDir))
        {
            return;
        }

        var manifestSet = manifest.Files
            .Select(f => f.Path.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var keptPrefixes = PreservedEntries
            .Select(p => p.Replace('\\', '/') + "/")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(installDir, path).Replace('\\', '/');
            var top = relative.Contains('/')
                ? relative[..relative.IndexOf('/')]
                : relative;

            if (keptPrefixes.Contains(top + "/") ||
                PreservedEntries.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!manifestSet.Contains(relative))
            {
                File.Delete(path);
            }
        }
    }

    private static void Report(
        IProgress<UpdateProgress>? progress,
        UpdatePhase phase,
        long totalBytes,
        long downloadedBytes,
        int filesDone,
        int filesTotal,
        string? currentItem) => progress?.Report(
        new UpdateProgress(phase, totalBytes, downloadedBytes, filesDone, filesTotal, currentItem));
}
