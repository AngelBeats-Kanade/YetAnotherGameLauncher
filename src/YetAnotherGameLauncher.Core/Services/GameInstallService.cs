using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Core.Services;

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
    private readonly IDownloader _downloader = downloader;
    private readonly GameInstallServiceOptions _options = options ?? new();

    /// <summary>不在清单内也要保留的顶层目录与文件（存档、启动器自身数据、官方启动器兼容文件）。</summary>
    public static readonly IReadOnlyList<string> PreservedEntries = [".yagl", "Saved", "launcherDownloadConfig.json"];

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

        logger?.LogInformation("同步 {Version}：{Total} 个文件中需要下载 {Needed} 个",
            manifest.Version, manifest.Files.Count, needed.Count);

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
                    throw new UpdateException($"清单条目缺少下载地址：{file.Path}");
                }

                var destination = ManifestVerifier.ResolveSafe(installDir, file.Path);
                var perFile = new Progress<long>(bytes =>
                {
                    long total;
                    string? current;
                    lock (gate)
                    {
                        current = file.Path;
                        total = Interlocked.Read(ref downloadedBytes);
                    }
                    Report(progress, UpdatePhase.Downloading, totalBytes, total, filesDone, needed.Count, current);
                });

                await _downloader.DownloadFileAsync(
                    new DownloadRequest(file.Url, destination, file.Size, file.Md5),
                    perFile,
                    token);

                lock (gate)
                {
                    downloadedBytes += file.Size;
                    filesDone++;
                }
                Report(progress, UpdatePhase.Downloading, totalBytes, downloadedBytes, filesDone, needed.Count, file.Path);
            });

        Report(progress, UpdatePhase.Verifying, totalBytes, downloadedBytes, needed.Count, needed.Count, null);

        var post = ManifestVerifier.VerifyFull(installDir, manifest);
        if (!post.IsComplete)
        {
            var broken = string.Join("、", post.NeedsDownload.Select(n => $"{n.Path}({n.Status})"));
            throw new UpdateException($"下载完成后完整性校验失败，问题文件：{broken}");
        }

        Report(progress, UpdatePhase.CleaningUp, totalBytes, downloadedBytes, needed.Count, needed.Count, null);
        CleanupStaleFiles(installDir, manifest);

        Report(progress, UpdatePhase.Done, totalBytes, downloadedBytes, needed.Count, needed.Count, null);
    }

    /// <summary>删除清单之外的游离文件（跳过存档与启动器数据目录）。</summary>
    public static void CleanupStaleFiles(string installDir, GameManifest manifest)
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
