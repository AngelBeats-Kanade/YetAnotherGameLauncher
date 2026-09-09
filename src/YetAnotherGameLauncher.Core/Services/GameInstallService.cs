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

    /// <summary>
    /// 对照清单把安装目录补齐到目标版本：快速校验 → 只并行下载缺失/损坏文件 → 全量 MD5 校验
    /// （尺寸相同但内容损坏的文件逃得过快速校验，在此按 MD5 结论补下载一轮再验）→ 清理游离文件。
    /// 返回实际下载的文件数（即修复数）。
    /// </summary>
    public async Task<int> SyncAsync(
        string installDir,
        GameManifest manifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new UpdateProgress(UpdatePhase.Checking, 0, 0, 0, manifest.Files.Count, null));

        // 逐文件进度：大库扫描/MD5 动辄数分钟，没有增量反馈用户会以为卡死
        void ReportChecked(int checkedCount, int total) =>
            progress?.Report(new UpdateProgress(UpdatePhase.Checking, 0, 0, checkedCount, total, null));
        void ReportVerified(int checkedCount, int total) =>
            progress?.Report(new UpdateProgress(UpdatePhase.Verifying, 0, 0, checkedCount, total, null));

        var verification = ManifestVerifier.VerifyFast(installDir, manifest, ReportChecked);
        var state = new SyncProgressState();
        await DownloadBatchAsync(
            installDir,
            verification.NeedsDownload
                .Join(manifest.Files, n => n.Path, f => f.Path, (_, f) => f)
                .ToList(),
            progress, state, cancellationToken).ConfigureAwait(false);

        Report(progress, UpdatePhase.Verifying, state, null);

        var post = ManifestVerifier.VerifyFull(installDir, manifest, ReportVerified);
        if (!post.IsComplete)
        {
            // 快速校验只比存在性与大小：按 MD5 结论补下载一轮（多数情况为 0 个）后复验
            var broken = post.NeedsDownload
                .Join(manifest.Files, n => n.Path, f => f.Path, (_, f) => f)
                .ToList();
            logger?.LogInformation("Sync {Version}: {Broken} file(s) failed md5 verification, re-downloading",
                manifest.Version, broken.Count);
            await DownloadBatchAsync(installDir, broken, progress, state, cancellationToken).ConfigureAwait(false);

            post = ManifestVerifier.VerifyFull(installDir, manifest);
            if (!post.IsComplete)
            {
                var details = string.Join("、", post.NeedsDownload.Select(n => $"{n.Path}({n.Status})"));
                throw new UpdateException($"Post-download verification failed. Broken files: {details}");
            }
        }

        Report(progress, UpdatePhase.CleaningUp, state, null);
        CleanupStaleFiles(installDir, manifest);
        Report(progress, UpdatePhase.Done, state, null);
        return state.FilesDone;
    }

    /// <summary>并行下载一批清单文件，进度累计进共享状态（多轮下载共用同一进度骨架）。</summary>
    private async Task DownloadBatchAsync(
        string installDir,
        IReadOnlyList<ManifestFile> files,
        IProgress<UpdateProgress>? progress,
        SyncProgressState state,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return;
        }

        state.TotalBytes += files.Sum(f => f.Size);
        state.FilesTotal += files.Count;
        Report(progress, UpdatePhase.Downloading, state, null);

        var gate = new object();
        await Parallel.ForEachAsync(
            files,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxParallelFiles,
                CancellationToken = cancellationToken,
            },
            async (file, token) =>
            {
                ManifestChecks.EnsureDownloadUrl(file.Url, "Manifest entry", file.Path);

                var destination = ManifestVerifier.ResolveSafe(installDir, file.Path);

                // 并行下载共享计数：per-file 字节回调只在 lock 内读累计值，
                // 文件完成时才累加并上报（避免每字节回调高频打散 UI 进度条）
                var perFile = new Progress<long>(_ =>
                {
                    lock (gate)
                    {
                        Report(progress, UpdatePhase.Downloading, state, file.Path);
                    }
                });

                await downloader.DownloadFileAsync(
                    new DownloadRequest(file.Url, destination, file.Size, file.Md5),
                    perFile,
                    token).ConfigureAwait(false);

                lock (gate)
                {
                    state.DownloadedBytes += file.Size;
                    state.FilesDone++;
                }

                Report(progress, UpdatePhase.Downloading, state, file.Path);
            }).ConfigureAwait(false);
    }

    /// <summary>一轮同步的进度累计（多轮下载共享：总字节/已完成字节/文件数随之增长）。</summary>
    private sealed class SyncProgressState
    {
        public long TotalBytes;
        public long DownloadedBytes;
        public int FilesDone;
        public int FilesTotal;
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
        SyncProgressState state,
        string? currentItem) => progress?.Report(
        new UpdateProgress(phase, state.TotalBytes, state.DownloadedBytes, state.FilesDone, state.FilesTotal, currentItem));
}
