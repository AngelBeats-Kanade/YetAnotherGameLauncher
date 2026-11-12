using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

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

    /// <summary>不在清单内也要保留的顶层目录与文件（存档、启动器自身数据、官方启动器兼容文件、Wine prefix）。</summary>
    /// <remarks>
    /// 与 <see cref="ManifestBoundary.ReservedDirectories"/>（清单<b>禁写</b>名单）同源但**不等**：
    /// 本表是同步收尾清理的<b>保留</b>名单，有意排除 <c>launcherDownloadConfig</c>（官方启动器状态
    /// 目录）——"清理策略保持现状、不兼容官方启动器"是 2026-10-23 的用户决定，混用官方启动器时该目录
    /// 内容会被当游离文件删除，属已接受代价。两份名单共用一处定义、只在这里点名差异，避免各写一套而漂移。
    /// compatdata：旧版推荐配置曾把 Proton prefix 放在安装目录下（prefix 内是注册表/着色器缓存/用户数据，
    /// 被清掉等于毁掉游戏环境）。新配置已把 prefix 统一迁到应用数据目录，此处保留是纵深防御。
    /// </remarks>
    private static readonly IReadOnlyList<string> PreservedEntries =
        [
            .. ManifestBoundary.ReservedDirectories.Where(
                d => !string.Equals(d, ManifestBoundary.LauncherDownloadConfigDirectory, StringComparison.Ordinal)),
            ManifestBoundary.ReservedFile,
        ];

    /// <summary>
    /// 对照清单把安装目录补齐到目标版本：快速校验 → 只并行下载缺失/损坏文件 → MD5 事后校验
    /// （校验范围由 <paramref name="verifyAllExistingFiles"/> 决定，见其说明）→ 清理游离文件。
    /// 返回实际下载的文件数（即修复数）。
    /// </summary>
    /// <param name="installDir">游戏安装目录。</param>
    /// <param name="manifest">目标版本清单。</param>
    /// <param name="progress">进度报告；null = 不报告。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="verifyAllExistingFiles">
    /// true（默认，安装/更新/显式校验修复）= 下载后对整份清单做全量 MD5，能发现已有文件的同尺寸损坏；
    /// false（补档快速路径，2026-10-23）= 已有文件只信快速校验（存在性 + 尺寸），只对本次下载的文件
    /// 做 MD5 后验——补 UHD 时不必把整个 common 重算一遍。要全盘体检请走 true 的显式校验修复。
    /// false 下若下载窗口内已有文件被删/改尺寸（罕见），把这些文件当第二轮下载补下并 MD5 后验，
    /// **不**扩大到其余已有文件（无关的同尺寸损坏仍是快速路径容忍的）。
    /// </param>
    public async Task<int> SyncAsync(
        string installDir,
        GameManifest manifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool verifyAllExistingFiles = true)
    {
        progress?.Report(new UpdateProgress(UpdatePhase.Checking, 0, 0, 0, manifest.Files.Count, null));

        // 逐文件进度：大库扫描/MD5 动辄数分钟，没有增量反馈用户会以为卡死
        void ReportChecked(int checkedCount, int total) =>
            progress?.Report(new UpdateProgress(UpdatePhase.Checking, 0, 0, checkedCount, total, null));

        var verification = ManifestVerifier.VerifyFast(installDir, manifest, ReportChecked);
        var state = new SyncProgressState();
        var downloaded = verification.NeedsDownload
            .Join(manifest.Files, n => n.Path, f => f.Path, (_, f) => f)
            .ToList();
        await DownloadBatchAsync(installDir, downloaded, progress, state, cancellationToken).ConfigureAwait(false);

        Report(progress, UpdatePhase.Verifying, state, null);

        if (verifyAllExistingFiles)
        {
            // 全量语义：整份清单体检，任何 MD5 不符（含已有文件的同尺寸损坏）都补下载后再验
            await VerifyAndRepairRoundAsync(
                installDir, manifest, manifest.Files, state, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 补档快速路径：已有文件只信快速校验（同尺寸损坏留给显式校验修复），
            // 本次下载的文件仍做 MD5 后验（下载器/测试替身都可能写坏，不能只信下载返回值）。
            logger?.LogInformation(
                "Sync {Version}: fast pack-add verification; {Existing} existing file(s) size-checked, " +
                "{Downloaded} downloaded file(s) md5-verified.",
                manifest.Version, manifest.Files.Count - downloaded.Count, downloaded.Count);
            await VerifyAndRepairRoundAsync(
                installDir, manifest, downloaded, state, progress, cancellationToken).ConfigureAwait(false);

            // 下载窗口内已有文件被删/改尺寸（罕见，快速校验层可见）：把它们当作第二轮"本次下载"
            // 处理——同样补下载、同样 MD5 后验；不扩大到未受影响的已有文件（同尺寸损坏是快速路径
            // 显式容忍的，留给「校验修复」）。"绝不把不完整安装当成功"以快速校验口径为准，
            // 否则一个与本次下载无关的损坏文件会把罕见并发场景升级成硬失败（2026-10-23 复审）。
            var fastPost = ManifestVerifier.VerifyFast(installDir, manifest);
            if (!fastPost.IsComplete)
            {
                var late = fastPost.NeedsDownload
                    .Join(manifest.Files, n => n.Path, f => f.Path, (_, f) => f)
                    .ToList();
                logger?.LogWarning(
                    "Sync {Version}: {Count} existing file(s) changed during fast pack-add sync; repairing them.",
                    manifest.Version, late.Count);
                await VerifyAndRepairRoundAsync(
                    installDir, manifest, late, state, progress, cancellationToken).ConfigureAwait(false);
            }
        }

        Report(progress, UpdatePhase.CleaningUp, state, null);
        CleanupStaleFiles(installDir, manifest);
        Report(progress, UpdatePhase.Done, state, null);
        return state.FilesDone;
    }

    /// <summary>
    /// 一轮"MD5 后验 → 按结论补下载 → 复验"，范围限 <paramref name="scope"/>：不符的补下载一轮
    /// 再复验，仍不符才抛错。全量路径用它体检整份清单（scope = manifest.Files）；补档快速路径用它
    /// 守住"只校验本次下载的文件"（scope = downloaded / 罕见回退的 late）。
    /// </summary>
    private async Task VerifyAndRepairRoundAsync(
        string installDir,
        GameManifest manifest,
        IReadOnlyList<ManifestFile> scope,
        SyncProgressState state,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        void ReportVerified(int checkedCount, int total) =>
            progress?.Report(new UpdateProgress(UpdatePhase.Verifying, 0, 0, checkedCount, total, null));

        var post = ManifestVerifier.VerifyFull(installDir, Subset(manifest, scope), ReportVerified);
        if (post.IsComplete)
        {
            return;
        }

        // 快速校验只比存在性与大小：按 MD5 结论补下载一轮（多数情况为 0 个）后复验
        var broken = post.NeedsDownload
            .Join(scope, n => n.Path, f => f.Path, (_, f) => f)
            .ToList();
        logger?.LogInformation("Sync {Version}: {Broken} file(s) failed md5 verification, re-downloading",
            manifest.Version, broken.Count);
        await DownloadBatchAsync(installDir, broken, progress, state, cancellationToken).ConfigureAwait(false);

        post = ManifestVerifier.VerifyFull(installDir, Subset(manifest, scope));
        if (!post.IsComplete)
        {
            var details = string.Join("、", post.NeedsDownload.Select(n => $"{n.Path}({n.Status})"));
            throw new UpdateException($"Post-download verification failed. Broken files: {details}");
        }
    }

    /// <summary>只含指定文件的清单视图（MD5 后验用；不改变版本/保留前缀语义）。</summary>
    private static GameManifest Subset(GameManifest manifest, IReadOnlyList<ManifestFile> files) => new()
    {
        Version = manifest.Version,
        Files = files,
        ResourceTier = manifest.ResourceTier,
        PreservedPaths = manifest.PreservedPaths,
    };

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
        // 字节级实时进度：并行下载的 per-file 字节回调换算为全局增量，100ms 节流投递
        //（2026-10-02 用户点名"进度条随时更新"；此前 per-file 回调被丢弃、只在文件完成时整量跳变）
        var aggregator = new ByteProgressAggregator(TimeSpan.FromMilliseconds(100), bytes =>
        {
            lock (gate)
            {
                state.DownloadedBytes = bytes;
                Report(progress, UpdatePhase.Downloading, state, null);
            }
        });
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

                var perFile = aggregator.CreateFileProgress();

                await downloader.DownloadFileAsync(
                    new DownloadRequest(file.Url, destination, file.Size, file.Md5),
                    perFile,
                    token).ConfigureAwait(false);

                // 下载器零回调/回调与声明 size 有差时兜底补齐（负 delta 为重下回退的修正）——
                // 完成时全局恰好 +file.Size，分母分子同口径
                aggregator.Add(file.Size - perFile.ConsumedBytes);
                aggregator.ForceReport();

                lock (gate)
                {
                    state.FilesDone++;
                    state.DownloadedBytes = aggregator.Bytes;
                    Report(progress, UpdatePhase.Downloading, state, file.Path);
                }
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
    /// <remarks>存档包含根级 <c>Saved/</c> 与 UE 的 <c>Client/Saved/</c>：3.8+ 清单只点名
    /// <c>Client/Saved/Resources/&lt;版本&gt;/</c> 下的官方可选包，其余 <c>Client/Saved</c> 内容
    /// （SaveGames/Logs/Config）在清单外，必须整段保留，不能当游离文件删除。</remarks>
    private void CleanupStaleFiles(string installDir, GameManifest manifest)
    {
        if (!Directory.Exists(installDir))
        {
            return;
        }

        // 空清单安全闸（2026-10-26 复审 P1）：空清单 = "清单内什么也没有"，此时把清单外文件全删
        // 等于抹掉整个安装（common/HD 几十 GB）。渠道侧已拒收空全量清单，这里再做一道不依赖
        // 渠道的兜底：跳过清理并告警，宁可留垃圾也不误删。
        if (manifest.Files.Count == 0)
        {
            logger?.LogWarning(
                "Skipping stale-file cleanup: the manifest lists no files " +
                "(an empty manifest would delete the whole install).");
            return;
        }

        // 与清单边界共用同一条归一串（2026-10-26 复审）：清单里的 `a/./b`、`data/../x` 与磁盘上的
        // `a/b`、`x` 是同一目标；只比原始串会把"清单内文件"当游离文件删掉。当前渠道校验层会拒收
        // 这类写法，但 SyncAsync 是公开入口，不能假设上游一定校验过。
        static string NormalizeForCleanup(string path) =>
            ManifestBoundary.CanonicalizeWithinRoot(path) ?? path.Replace('\\', '/');

        // 平台差异系有意：大小写不敏感比较在 Linux 上会把大小写仅异的文件误判为"清单内"而保留
        // （只会少删、不会误删用户数据），比 Ordinal 激进删除更安全
        var manifestSet = manifest.Files
            .Select(f => NormalizeForCleanup(f.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var keptPrefixes = PreservedEntries
            .Select(p => p.Replace('\\', '/') + "/")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 渠道点名的保留前缀（档位模式下 = 未选中档位的资源目录，2026-10-19）：整段相对路径前缀匹配——
        // 档位目录不在本次清单内时绝不能当游离文件删掉（删除只经用户的显式「清理其他档位」动作）。
        // 首尾斜杠都归一：清单路径是无前导斜杠的相对路径，多写的斜杠只应失效在"误删"一侧（宽松保留）
        var preservedPrefixes = ManifestVerifier.NormalizePreservedPrefixes(manifest.PreservedPaths);

        // ReparsePoint：绝不穿过目录符号链接/junction 递归——用户把安装目录内的子目录搬到
        // 别的盘再链接回来（Windows 玩家常见的搬盘手法）时，词法相对路径会把链接目标处的
        // 真实文件判成"清单外"并删到安装目录之外；跳过链接子树即守住"清理绝不越出 installDir"。
        // 注意该选项同时把文件符号链接排除出枚举（链接本体不清、只留垃圾，无数据风险）——有意行为。
        // IgnoreInaccessible：单个无权限子目录会被静默跳过（枚举无回调可日志），
        // 已在 docs/GAME_CONFIG.md 清理小节注明。
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var path in Directory.EnumerateFiles(installDir, "*", enumerationOptions))
        {
            var relative = NormalizeForCleanup(Path.GetRelativePath(installDir, path));

            // UE 存档/日志/设置目录：清单只可能点名 Client/Saved/Resources/<版本>/，
            // 其余内容（SaveGames/Logs/Config）不在清单里，必须整段保留。
            if (ManifestBoundary.IsClientSavedPath(relative))
            {
                continue;
            }

            var top = relative.Contains('/')
                ? relative[..relative.IndexOf('/')]
                : relative;

            if (keptPrefixes.Contains(top + "/") ||
                PreservedEntries.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ManifestVerifier.IsUnderPreservedPrefix(relative, preservedPrefixes))
            {
                continue;
            }

            if (!manifestSet.Contains(relative))
            {
                // 单文件删除失败（只读挂载/权限异常）只跳过该文件并记日志，
                // 不让一个坏文件中断整轮同步
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger?.LogWarning(ex, "清理游离文件失败（跳过）：{Path}", path);
                }
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
