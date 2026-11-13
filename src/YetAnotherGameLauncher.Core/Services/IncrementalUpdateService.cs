using System.Text.Json;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// 增量更新（两段式，与 wutheringwaves-cli-manager 的 predownload --apply 对齐）：
/// 1) Predownload：把差分包（krpdiff）与可直接下载的新文件放入 {installDir}/.yagl/predownload 暂存
///    （重跑保留暂存：完好文件核验跳过、.temp 续传）；
/// 2) Apply：逐组"复制旧文件 → hpatchz 合成 → MD5 校验 → .yagl-bak 备份替换"（单组失败自动回滚
///    该组，中断后可重新执行；每组应用前做空闲空间预检，成功后立即回收本组草稿 olddir/newdir 与
///    已消费补丁——峰值 = 单组 src+dst，不随总载荷增长，2026-11-13 3.8 事故修复），随后落位暂存
///    新文件、删除清单废弃文件（deleteFiles 在组后——废弃文件可能同时是组差分源，2026-10-02 真机
///    P1 实锤）、清理暂存；差分源缺失、补丁器执行失败或补丁已被按组回收（目标随后被改动）时按
///    注入的解析缝直下该组产物自救（组级回退）。
/// </summary>
public sealed class IncrementalUpdateService(
    IDownloader downloader,
    IPatchApplier patchApplier,
    ILogger? logger = null,
    TimeSpan? progressReportInterval = null)
{
    /// <summary>预下载暂存目录名，挂在 {installDir}/.yagl 之下。</summary>
    public const string PredownloadDirName = "predownload";

    /// <summary>暂存清单文件名（预下载目录内；增量与包式两条链写同一个文件）。</summary>
    public const string StagedManifestFileName = "manifest.json";
    private const string PatchWorkDirName = "patchwork";
    private const string BackupSuffix = ".yagl-bak";


    /// <summary>某安装目录的预下载暂存目录（{installDir}/.yagl/predownload）。</summary>
    public static string PredownloadDir(string installDir) =>
        Path.Combine(installDir, LocalStateService.StateDirName, PredownloadDirName);

    /// <summary>把暂存对应的清单写入暂存目录（应用预下载时按此核对）；增量与包式预下载链路共用。
    /// 原子写：数十 GB 预下载完成后 manifest.json 写到一半被杀（断电/占用截断）会让全部预下载
    /// 作废（读回即损坏 → Apply 报"No preloaded update found"）（2026-09-20 复审修复）。</summary>
    public static async Task WriteStagedManifestAsync(
        string staging, GameManifest manifest, CancellationToken cancellationToken)
    {
        await FileUtilities.WriteAtomicAsync(
            Path.Combine(staging, StagedManifestFileName),
            JsonSerializer.Serialize(manifest, Json.Default),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 操作提交后清掉暂存（.yagl/predownload 整棵：此时内容已落盘/已消费）。提交前必须保留：
    /// 修复轮/状态写盘掉线后重跑靠暂存清单进入"应用链续跑"（组级应用幂等），不重下整包差量
    /// （2026-10-19 review 修复）。
    /// </summary>
    public static void ClearStaging(string installDir, ILogger? logger = null) =>
        FileUtilities.TryDeleteDirectory(PredownloadDir(installDir), logger);

    private static string PatchWorkDir(string installDir) =>
        Path.Combine(installDir, LocalStateService.StateDirName, PatchWorkDirName);

    /// <summary>
    /// 清单相对路径 → basePath 下的落点：归一 '\'→'/' 后拒绝根路径与 ".." 段再拼合。
    /// 安装目录内的落位走 <see cref="ManifestVerifier.ResolveSafe"/>，暂存/工作目录内的拼合
    /// 用本函数补上同一道防线——清单不可信（HTTPS 只保证传输），"../" 两种平台都是穿越，
    /// "..\\" 在 Windows 上是穿越、Linux 上是普通文件名，平台不对称，统一按穿越拒绝。
    /// </summary>
    internal static string SafeJoin(string basePath, string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new UpdateException($"Manifest path escapes sandbox: {relative}");
        }

        return Path.Combine(basePath, normalized.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>阶段一：把差分包与新文件下载到暂存目录（MD5/大小校验），并写入暂存清单供 Apply 使用。
    /// 重跑语义（2026-10-02 对齐 ww-manager）：暂存不再整树重置——完好文件按条目核验跳过、
    /// .temp 由下载器 Range 续传，失败后重跑只补缺失部分（此前整树清空会把已下载的几十 GB
    /// 全部作废重下）。清单版本切换后的孤儿残留由 Apply 成功时的整目录清理回收。</summary>
    public async Task PredownloadAsync(
        string installDir,
        GameManifest incrementalManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var staging = PredownloadDir(installDir);
        Directory.CreateDirectory(staging);

        var totalBytes = incrementalManifest.Files.Sum(f => f.Size)
                         + incrementalManifest.Groups.Sum(g => g.PatchSize);
        var totalItems = incrementalManifest.Files.Count + incrementalManifest.Groups.Count;
        var done = 0;
        var current = (string?)null;

        // 字节级实时进度：下载器 64KB 粒度回调 → 全局增量 → 100ms 节流投递
        //（进度口径 = 本地已就绪字节，续传起点含在下载器首回调的 delta 里）
        var aggregator = new ByteProgressAggregator(
            progressReportInterval ?? TimeSpan.FromMilliseconds(100), bytes =>
            progress?.Report(new UpdateProgress(UpdatePhase.Downloading, totalBytes, bytes, done, totalItems, current)));

        progress?.Report(new UpdateProgress(UpdatePhase.Downloading, totalBytes, 0, 0, totalItems, null));

        // 两类暂存条目（直下新文件 / 差分包）下载流程一致：同一本地函数处理并聚合进度
        //（下载为顺序执行，无并发访问，计数无需加锁）
        async Task DownloadStagedAsync(string url, long size, string? md5, string relativeTarget, string displayName)
        {
            var target = SafeJoin(staging, relativeTarget);
            current = displayName;

            // 核验阶段与下载共用同一进度管道：已暂存条目的逐块 MD5 读取经 FileProgress 计入全局
            // 字节（2026-10-02 用户报障：暂存齐备时重跑预下载进度 0→100 闪过、无速度——速度/文案
            // 由 VM 既有 Downloading 链路复用呈现）
            var verifyProgress = aggregator.CreateFileProgress();
            if (await IsAlreadyStaged(target, size, md5, cancellationToken, verifyProgress).ConfigureAwait(false))
            {
                logger?.LogDebug("Staged file already complete, skipping download: {Target}", target);
                done++;
                aggregator.Add(size - verifyProgress.ConsumedBytes);
                aggregator.ForceReport();
                return;
            }

            // 核验半途判未就绪（size 不符/MD5 不符）：已计入的核验字节负增量回吐，防进度虚高；
            // 下载另建新 FileProgress（续传起点口径，不与核验实例混用）
            aggregator.Add(-verifyProgress.ConsumedBytes);
            var fileProgress = aggregator.CreateFileProgress();
            await downloader.DownloadFileAsync(
                new DownloadRequest(url, target, size, md5), fileProgress, cancellationToken).ConfigureAwait(false);
            done++;
            // 下载器零回调（替身实现）/期望与实收有差时兜底补齐；回调过量（重下回退）时负修正
            //——完成时全局恰好 +size，与分母同口径
            aggregator.Add(size - fileProgress.ConsumedBytes);
            aggregator.ForceReport();
        }

        foreach (var file in incrementalManifest.Files)
        {
            ManifestChecks.EnsureDownloadUrl(file.Url, "Incremental manifest entry", file.Path);

            await DownloadStagedAsync(file.Url, file.Size, file.Md5, Path.Combine("files", file.Path), file.Path).ConfigureAwait(false);
        }

        foreach (var group in incrementalManifest.Groups)
        {
            ManifestChecks.EnsureDownloadUrl(group.Url, "Patch group", group.PatchFile);

            await DownloadStagedAsync(group.Url, group.PatchSize, group.PatchMd5, Path.Combine("patches", group.PatchFile), group.PatchFile).ConfigureAwait(false);
        }

        await WriteStagedManifestAsync(staging, incrementalManifest, cancellationToken).ConfigureAwait(false);

        logger?.LogInformation("Predownload finished: {Groups} patch groups, {Files} files", incrementalManifest.Groups.Count, incrementalManifest.Files.Count);
        progress?.Report(new UpdateProgress(UpdatePhase.Done, totalBytes, aggregator.Bytes, totalItems, totalItems, null));
    }

    /// <summary>暂存条目是否已完整就绪：存在 + size 匹配 + MD5 匹配（期望缺失的项跳过对应校验，
    /// 与 <see cref="HttpFileDownloader"/> 的 Verify 同语义）。拒读/占用按未就绪处理，
    /// 交回下载路径由其折算可操作错误。MD5 走流式异步哈希（2026-10-02 假死修复：全量重跑核验
    /// 可达数十 GiB，不长期占用调用线程、可取消），逐块读取经 <paramref name="progress"/> 上报
    /// 累计已读字节——调用方以 FileProgress 承接，核验进度复用下载管道。</summary>
    private static async Task<bool> IsAlreadyStaged(
        string path,
        long size,
        string? md5,
        CancellationToken cancellationToken,
        IProgress<long>? progress = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (size > 0 && new FileInfo(path).Length != size)
            {
                return false;
            }

            return string.IsNullOrEmpty(md5)
                || string.Equals(
                    await Hashing.Md5HexAsync(path, cancellationToken, progress).ConfigureAwait(false),
                    md5, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>读取暂存清单；没有已预下载内容时返回 null。文件被占用/无权限同样按
    /// "暂存未知"处理返回 null（与 <see cref="LocalStateService.Load"/> 同语义）——
    /// 只捕 JsonException 会让 Windows 上杀软瞬时锁住 manifest.json 时异常穿出
    /// RefreshAsync 的弃元调用点，静默丢失状态刷新与完成提示（2026-09-20 复审修复）。</summary>
    public static GameManifest? TryLoadStagedManifest(string installDir)
    {
        var path = Path.Combine(PredownloadDir(installDir), StagedManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GameManifest>(File.ReadAllText(path), Json.Default);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>阶段二：应用暂存的差分。中断/失败后可重复调用直至成功。</summary>
    /// <param name="installDir">游戏安装目录。</param>
    /// <param name="incrementalManifest">暂存的增量清单（TryLoadStagedManifest 读回）。</param>
    /// <param name="progress">进度回调（Verifying/Patching/Done 阶段）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="dstUrlResolver">组级回退的产物直链解析缝（相对路径 → CDN 直链，查无返回 null）。
    /// 差分源缺失（官方清单 deleteFiles ∩ srcFiles 交叉或安装被外力破坏）、补丁器执行失败，或该组
    /// 补丁已被按组回收后目标又被改动时，按组直下产物自救；null = 不启用回退（按可操作报错收尾）。</param>
    public async Task ApplyAsync(
        string installDir,
        GameManifest incrementalManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<string, CancellationToken, Task<string?>>? dstUrlResolver = null)
    {
        // UI 线程让位（2026-10-02 真机假死实锤）：VM 命令在 UI 线程 await 本方法，若无先行让位，
        // patchwork 清理/组校验 MD5/deleteFiles（重入时全部已应用组 dstFiles 可达数十 GiB）会同步
        // 跑在 UI 线程直到首个真实 await——入口强制异步续体落线程池（ForceYielding 不捕获上下文；
        // Task.Yield 不可用：YieldAwaitable 无 ConfigureAwait，会把续体贴回 UI 上下文）
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        var staging = PredownloadDir(installDir);
        var workDir = PatchWorkDir(installDir);
        FileUtilities.TryDeleteDirectory(workDir, logger);

        var total = incrementalManifest.Groups.Count;
        var patchDir = Path.Combine(staging, "patches");
        for (var i = 0; i < total; i++)
        {
            var group = incrementalManifest.Groups[i];
            cancellationToken.ThrowIfCancellationRequested();

            // 重入校验先行报点（Verifying 阶段）：已应用组的 dstFiles 全量 MD5 可达分钟级，
            // 期间 UI 必须有阶段反馈而非停留在旧状态
            progress?.Report(new UpdateProgress(UpdatePhase.Verifying, group.PatchSize * (total - i), 0, i, total, group.PatchFile));

            if (await GroupAlreadyAppliedAsync(installDir, group, cancellationToken).ConfigureAwait(false))
            {
                logger?.LogDebug("Target file of group {Patch} already present; skipped", group.PatchFile);
                // 已应用组的补丁不再消费：续跑时它只是占盘的遗留，就地回收（本轮末尾清理只在
                // 整轮成功时执行，失败/取消轮不该让上一轮的补丁继续滞留）
                TryDeleteFile(SafeJoin(patchDir, group.PatchFile), logger);
                continue;
            }

            progress?.Report(new UpdateProgress(UpdatePhase.Patching, group.PatchSize * (total - i), 0, i, total, group.PatchFile));

            var patchPath = SafeJoin(patchDir, group.PatchFile);
            var patchStaged = File.Exists(patchPath);
            if (!patchStaged && dstUrlResolver is null)
            {
                throw new UpdateException(
                    $"Patch {group.PatchFile} was not preloaded; run predownload first or use the full update.");
            }

            EnsureEnoughFreeSpaceForGroup(installDir, group, includesSourceCopy: patchStaged);

            if (patchStaged)
            {
                await ApplyGroupAsync(installDir, workDir, i, group, patchPath, dstUrlResolver, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // 补丁已被按组回收（本方法成功后即删）：若该组目标随后被改动（用户为腾空间删了游戏
                // 文件、磁盘损坏），续跑会判"未应用"而差分不可用。有解析缝时按既有组级回退直下产物
                // 自救，避免把用户卡在不可操作的 "run predownload first"（暂存清单在场时 UI 不给
                // 预下载入口）。无解析缝保持原报错（旧调用方兼容）。
                await ApplyGroupFromDirectDownloadAsync(
                    installDir, workDir, i, group, dstUrlResolver!, progress, cancellationToken).ConfigureAwait(false);
            }

            // 组成功即回收本组草稿与已消费补丁（2026-11-13 3.8 事故：此前只在整轮 75 组全部成功后
            // 才删 workDir，峰值 ≈ 所有被替换文件的旧版本 + 暂存 ≈ 100G，把 479G 根分区写满；开
            // reflink 也只是把成本推迟到原文件被替换那一刻）。失败/取消保留当前组现场供诊断与续跑，
            // 下一轮 Apply 入口会整树清掉残留。
            FileUtilities.TryDeleteDirectory(GroupOldDir(workDir, i), logger);
            FileUtilities.TryDeleteDirectory(GroupNewDir(workDir, i), logger);
            TryDeleteFile(patchPath, logger);
        }

        // 差分组不覆盖的新文件已在预下载时暂存，此处落位；缺失的交给事后修复
        var stagedFilesDir = Path.Combine(staging, "files");
        foreach (var file in incrementalManifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = ManifestVerifier.ResolveSafe(installDir, file.Path);
            if (ManifestVerifier.CheckFile(target, file, withMd5: true) == FileStatus.Ok)
            {
                continue;
            }

            var stagedFile = SafeJoin(stagedFilesDir, file.Path);
            if (!File.Exists(stagedFile))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                File.Move(stagedFile, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 与 HttpFileDownloader.ReplaceDestination 同一防线：目标被占用/只读时重试不会自愈，
                // 给可操作错误而不是裸 IOException（2026-09-20 复审修复）
                throw new UpdateException(
                    $"Could not place staged file {file.Path}: {ex.Message}. " +
                    "Close apps using the file (or clear its read-only attribute) and retry the update.",
                    ex);
            }

            logger?.LogDebug("Staged file placed: {Path}", file.Path);
        }

        // 废弃文件删除（官方 deleteFiles）：必须在组循环与落位之后——官方清单存在 deleteFiles ∩
        // 组 srcFiles 交叉（2026-10-02 真机 P1 实锤：3.6.1→3.7.0 group_37 的 4 个差分源同时在
        // 废弃清单），先删会让组差分永久不可行；删除失败仍报错中止，重试安全（组幂等跳过 +
        // 删除重试，游戏内容此时已是目标版本）
        DeleteListedFiles(installDir, incrementalManifest, logger);

        // 载荷（补丁/暂存文件）已消费完，先释放（Windows 上残留被占用时尽力清理即可）；
        // 暂存清单 manifest.json 必须保留到操作提交（GameUpdateService 写状态）之后再清
        // （2026-10-19 review 修复）：此后还隔着修复轮与状态写盘两个可取消点，期间掉线/暂停时
        // 清单还在场，续跑走"应用链续跑"（组级应用按 dstFiles 校验幂等跳过）而不是重下整包差量。
        FileUtilities.TryDeleteDirectory(patchDir, logger);
        FileUtilities.TryDeleteDirectory(Path.Combine(staging, "files"), logger);
        await WriteStagedManifestAsync(staging, incrementalManifest, cancellationToken).ConfigureAwait(false);
        // 差分工作草稿同样清理：SrcFiles 的完整副本（olddir-*/newdir-*）可占数 GB~数十 GB，
        // .yagl 在安装同步的保留名单里不会被游走清理，留着即滞留到下次增量（2026-09-20 复审修复）
        FileUtilities.TryDeleteDirectory(workDir, logger);

        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, total, total, null));
    }

    /// <summary>删除清单点名的废弃文件（官方 deleteFiles，2026-10-02 真机实测顶层 6 条旧 pak/sig）。
    /// 位于组循环与落位之后（2026-10-02 修订，F88 裁定更新）：官方清单的废弃文件可能同时是后续
    /// 组的差分源（deleteFiles ∩ srcFiles 非空），先删会让组差分永久不可行且不可自愈；尾部删除
    /// 同样在游戏下次启动前清掉废弃文件，UE 挂载冲突不成立。不存在/目录条目跳过；删除失败
    /// （占用/只读）抛 UpdateException 中止——此时组差分与落位已完成且校验通过，重试安全。
    /// 产物豁免（RF-C，2026-10-03）：删除清单先扣除本次更新产物（Files ∪ 组 dstFiles）——官方
    /// 清单若自相矛盾地让废弃条目命中刚产出且校验通过的文件，删掉即无自愈路径（版本已前移、
    /// 增量清单不再含该文件）；豁免记警告跳过，下轮清单若坚持删除（届时不在产物集）仍会执行。</summary>
    private static void DeleteListedFiles(string installDir, GameManifest manifest, ILogger? logger)
    {
        // 产物豁免集合同样按归一串登记：否则 `./x`、`a/../x` 这类写法与裸名 `x` 是同一目标却匹配不上，
        // 会把刚产出且校验通过的文件删掉（2026-10-26 复审）。
        var producedThisUpdate = new HashSet<string>(StringComparer.Ordinal);
        void AddProduced(string path) =>
            producedThisUpdate.Add(ManifestBoundary.CanonicalizeWithinRoot(path) ?? path.Replace('\\', '/'));

        foreach (var file in manifest.Files)
        {
            AddProduced(file.Path);
        }

        foreach (var group in manifest.Groups)
        {
            foreach (var dst in group.DstFiles)
            {
                AddProduced(dst.Path);
            }
        }

        // 渠道点名的保留前缀（档位模式下 = 未选中档位目录，2026-10-19 review 实锤）：废弃清单点名保留
        // 目录内的内容同样不得隐式删除——"档位内容永不隐式删除、删除只经显式清理"是全仓不变量，
        // 渠道侧已按档位裁剪，这里是不依赖渠道实现的纵深守卫（漏裁只该少删、不该误删用户数据）
        var preservedPrefixes = ManifestVerifier.NormalizePreservedPrefixes(manifest.PreservedPaths);

        foreach (var relative in manifest.DeleteFiles)
        {
            // 空白条目（含旧暂存清单反序列化出的 null）不指向任何可删文件：跳过，别 NRE 掉整轮更新
            //（F42 同族，2026-10-26 复审）。
            if (string.IsNullOrWhiteSpace(relative))
            {
                logger?.LogWarning("deleteFiles has a blank entry; skipping it.");
                continue;
            }

            // 清单边界（2026-10-23）：deleteFiles 是外部输入，点名启动器状态/存档/官方兼容目录
            // 一律跳过。渠道层已过滤，这里是不依赖渠道实现的消费端纵深守卫（旧暂存清单也走此处）。
            if (ManifestBoundary.IsReservedPath(relative))
            {
                logger?.LogWarning(
                    "deleteFiles lists reserved path {Path}; sparing it (state/saves/official-launcher files are never deleted).",
                    relative);
                continue;
            }

            // 下面两项判定与产物豁免共用同一条归一串：`./x`、`a/../x`、尾点/尾空格形态在 Win32 下
            // 与裸名是同一目标，只比原始串会被它们绕过（删掉保留档位内容 / 刚产出的文件）。
            // 逃逸形态归一为 null → 回退原串，由下方 ResolveSafe 以 "escapes sandbox" 拒收。
            var canonical = ManifestBoundary.CanonicalizeWithinRoot(relative) ?? relative.Replace('\\', '/');

            if (ManifestVerifier.IsUnderPreservedPrefix(canonical, preservedPrefixes))
            {
                logger?.LogWarning(
                    "deleteFiles lists {Path} under a preserved prefix; sparing it (tier content is deleted only by the explicit cleanup).",
                    relative);
                continue;
            }

            // 逃逸路径由 ResolveSafe 直接抛 UpdateException（2026-10-02 三轮统一，F42 同族）
            var target = ManifestVerifier.ResolveSafe(installDir, relative);

            if (producedThisUpdate.Contains(canonical))
            {
                // 自相矛盾清单（废弃条目同时是本次产物）：保护产出，警告暴露矛盾供上游排查
                logger?.LogWarning(
                    "deleteFiles lists {Path} which this update just produced; sparing it (contradictory manifest).",
                    relative);
                continue;
            }

            if (!File.Exists(target))
            {
                logger?.LogDebug("deleteFiles target missing, skipping: {Path}", relative);
                continue;
            }

            if (Directory.Exists(target))
            {
                logger?.LogDebug("deleteFiles target is a directory, skipping: {Path}", relative);
                continue;
            }

            try
            {
                File.Delete(target);
                logger?.LogDebug("Deleted obsolete file listed by manifest: {Path}", relative);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UpdateException(
                    $"Could not delete obsolete file {relative}: {ex.Message}. " +
                    "Close apps using the file (or clear its read-only attribute) and retry the update.", ex);
            }
        }
    }

    /// <summary>组草稿目录（SrcFiles 的完整副本）：按组号隔离，组成功后立即删除以压住峰值。</summary>
    private static string GroupOldDir(string workDir, int index) => Path.Combine(workDir, $"olddir-{index}");

    /// <summary>组产物目录（hpatchz/直下回退的输出）：替换进游戏目录后即无价值，组成功后删除。</summary>
    private static string GroupNewDir(string workDir, int index) => Path.Combine(workDir, $"newdir-{index}");

    /// <summary>
    /// 逐组空闲空间预检（2026-11-13 3.8 事故：Kuro 增量链此前没有预检，一路写到根分区 100% 才失败）。
    /// 需求按 src+dst 保守估算——**不假设 reflink**：有 CoW 的文件系统上实际只需要 dst，但 XFS/btrfs
    /// 的副本在源文件被替换后仍是旧数据的唯一引用、空间照常占用；ext4 等无 reflink 时本就是 src+dst。
    /// 探针给不出空闲值（平台/权限）直接放行：预检是防呆，不能变成新的阻断源。
    /// 必须逐**待应用**组判定，不能在循环前对全部组取 max——续跑时已应用完的最大组（3.8 实测单组
    /// 55.7 GiB）会永久误拦后续更新。
    /// <paramref name="includesSourceCopy"/> = 该组会复制 SrcFiles 到 olddir（差分路径）。缺补丁走直下
    /// 回退时只产出 DstFiles、不复制 src，按 dst-only 估算——否则恢复路径会被源文件声明尺寸误拦
    /// （用户为腾空间删掉源文件后尤其没道理），而它本来只下载一份产物。
    /// </summary>
    private static void EnsureEnoughFreeSpaceForGroup(string installDir, PatchGroup group, bool includesSourceCopy)
    {
        var free = DiskSpace.TryGetAvailableFreeBytes(installDir);
        if (free is null)
        {
            return;
        }

        var required = (includesSourceCopy ? group.SrcFiles.Sum(f => Math.Max(0, f.Size)) : 0)
                       + group.DstFiles.Sum(f => Math.Max(0, f.Size));
        if (free.Value >= required)
        {
            return;
        }

        var detail = includesSourceCopy
            ? "source copy + produced files, reflink not assumed"
            : "produced files only (direct download)";
        throw new UpdateException(
            $"Not enough free disk space for patch group {group.PatchFile}: " +
            $"{required / (double)(1L << 30):F2} GiB required ({detail}), " +
            $"{free.Value / (double)(1L << 30):F2} GiB available. " +
            "Free up space and retry - groups already applied are skipped and are not downloaded again.");
    }

    /// <summary>
    /// 补丁已被按组回收后的组级自救（2026-11-13，与源缺失/hpatchz 失败同族的组级回退）：组成功后
    /// 补丁即删，若该组目标随后被改动（用户为腾空间删了游戏文件、磁盘损坏），续跑判"未应用"时差分
    /// 已不可用。有解析缝时直下该组产物（传输量大，但免重下整包、免手动清暂存），无解析缝由调用方
    /// 保持原"run predownload first"报错。
    /// </summary>
    private async Task ApplyGroupFromDirectDownloadAsync(
        string installDir,
        string workDir,
        int index,
        PatchGroup group,
        Func<string, CancellationToken, Task<string?>> dstUrlResolver,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        logger?.LogWarning(
            "Patch {Patch} is no longer staged; downloading this group's outputs directly", group.PatchFile);
        var newDir = GroupNewDir(workDir, index);
        Directory.CreateDirectory(newDir);
        await DownloadGroupOutputsDirectlyAsync(
            newDir, group, $"patch {group.PatchFile} is no longer staged", dstUrlResolver, progress, cancellationToken).ConfigureAwait(false);
        VerifyGroupOutputs(newDir, group);
        ReplaceWithBackup(installDir, group, newDir, logger);
    }

    /// <summary>组产物事后校验（尺寸 + MD5，逐 dst）：不合规即整组失败，绝不落位坏内容。</summary>
    private static void VerifyGroupOutputs(string newDir, PatchGroup group)
    {
        foreach (var dst in group.DstFiles)
        {
            var produced = SafeJoin(newDir, dst.Path);
            if (!File.Exists(produced))
            {
                throw new UpdateException($"Patcher did not produce {dst.Path}; patch group {group.PatchFile} failed.");
            }

            var producedLength = new FileInfo(produced).Length;
            if (producedLength != dst.Size
                || !string.Equals(Hashing.Md5Hex(produced), dst.Md5, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException($"Checksum mismatch for patched {dst.Path}; patch group {group.PatchFile} failed.");
            }
        }
    }

    private async Task ApplyGroupAsync(
        string installDir,
        string workDir,
        int index,
        PatchGroup group,
        string patchPath,
        Func<string, CancellationToken, Task<string?>>? dstUrlResolver,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var oldDir = GroupOldDir(workDir, index);
        var newDir = GroupNewDir(workDir, index);
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);

        // 差分源缺失检测：官方清单存在 deleteFiles ∩ srcFiles 交叉（或安装被外力破坏）——组差分
        // 不可行时经解析缝直下该组产物自救（组级回退），无解析缝保持可操作报错（旧调用方兼容）
        var missingSrc = group.SrcFiles.FirstOrDefault(src =>
            !File.Exists(ManifestVerifier.ResolveSafe(installDir, src.Path)));
        if (missingSrc is not null)
        {
            await DownloadGroupOutputsDirectlyAsync(
                newDir, group, $"source file {missingSrc.Path} is missing", dstUrlResolver, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (var src in group.SrcFiles)
            {
                var source = ManifestVerifier.ResolveSafe(installDir, src.Path);

                var copied = SafeJoin(oldDir, src.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
                try
                {
                    File.Copy(source, copied, overwrite: true);
                    // 副本继承源文件属性：Windows 上只读游戏文件会让副本同样带只读位，按组清理与
                    // 整轮整树清理的 File.Delete 都会抛 UnauthorizedAccessException 并被尽力删除吞掉，
                    // olddir 滞留、峰值退化回整轮载荷（2026-11-13 复审 R1）。草稿是启动器自己的临时
                    // 副本，去掉只读位不影响游戏文件本身。
                    File.SetAttributes(copied, File.GetAttributes(copied) & ~FileAttributes.ReadOnly);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           && !cancellationToken.IsCancellationRequested)
                {
                    // 源文件拒读/被占用（Windows 杀软锁定、POSIX 权限剥夺）不是补丁本身失败，但同样
                    // 折算 UpdateException（F67）：File.Copy 原在下方折算 try 之外，裸异常会绕开
                    // "补丁失败统一折算"的分类纪律直穿调用方
                    throw new UpdateException(
                        $"Source file {src.Path} of patch group {group.PatchFile} could not be read: {ex.Message}", ex);
                }
            }

            try
            {
                await patchApplier.ApplyAsync(patchPath, oldDir, newDir, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // hpatchz 失败不再直接死刑（2026-10-03 backlog 激活：109 事件实锤官方 krpdiff 的
                // dir-diff 头部可整批击穿补丁器，与源缺失回退同族的 ww-manager 韧性语义）：
                // 有解析缝时直下该组产物自救，无解析缝保持原执行报错（旧调用方兼容）。
                // 取消不是补丁失败，OCE 穿透不回退。
                if (dstUrlResolver is null)
                {
                    throw new UpdateException($"Patch application failed ({group.PatchFile}): {ex.Message}", ex);
                }

                logger?.LogWarning(ex, "Patch application failed for {Patch}; falling back to direct downloads", group.PatchFile);

                try
                {
                    await DownloadGroupOutputsDirectlyAsync(
                        newDir, group, $"patch application failed: {ex.Message}", dstUrlResolver, progress, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
                {
                    // 两级根因都保留：补丁失败原因在前（主因），回退失败原因在后
                    throw new UpdateException(
                        $"Patch application failed ({group.PatchFile}): {ex.Message}; " +
                        $"direct-download fallback also failed: {fallbackEx.Message}", fallbackEx);
                }
            }
        }

        VerifyGroupOutputs(newDir, group);
        ReplaceWithBackup(installDir, group, newDir, logger);
    }

    /// <summary>组级回退：组差分不可行时直下该组产物（ww-manager 同族韧性语义）。两种触发形态：
    /// 差分源缺失（官方清单 deleteFiles ∩ srcFiles 交叉或安装被外力破坏，2026-10-02 真机 P1 实锤）
    /// 与 hpatchz 执行失败（2026-10-03 扩展），<paramref name="reason"/> 承载触发原因进日志与报错。
    /// 产物 URL 经 dstUrlResolver 解析，size+MD5 校验内建于下载请求，落位复用与正常组
    /// 相同的 dst 事后校验 + <see cref="ReplaceWithBackup"/> 原子替换；解析缝为 null 时保持
    /// 可操作报错（旧调用方向后兼容）。</summary>
    private async Task DownloadGroupOutputsDirectlyAsync(
        string newDir,
        PatchGroup group,
        string reason,
        Func<string, CancellationToken, Task<string?>>? dstUrlResolver,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (dstUrlResolver is null)
        {
            throw new UpdateException(
                $"Patch group {group.PatchFile} cannot be applied ({reason}); use the full update.");
        }

        logger?.LogInformation(
            "Patch group {Patch} cannot be applied differentially ({Reason}); downloading {Count} outputs directly",
            group.PatchFile, reason, group.DstFiles.Count);

        for (var i = 0; i < group.DstFiles.Count; i++)
        {
            var dst = group.DstFiles[i];
            cancellationToken.ThrowIfCancellationRequested();

            string? url;
            try
            {
                url = await dstUrlResolver(dst.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 解析缝内部故障（如全量清单抓取失败）同样折算可操作报错，不裸奔
                throw new UpdateException(
                    $"Could not resolve a direct download for patch group {group.PatchFile}: {ex.Message}", ex);
            }

            if (url is null)
            {
                throw new UpdateException(
                    $"Patch group {group.PatchFile} cannot be applied ({reason}) and output {dst.Path} is not " +
                    "available for direct download; use the full update.");
            }

            ManifestChecks.EnsureDownloadUrl(url, "Direct-download fallback", dst.Path);

            var target = SafeJoin(newDir, dst.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                await downloader.DownloadFileAsync(
                    new DownloadRequest(url, target, dst.Size, dst.Md5), null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new UpdateException(
                    $"Could not download {dst.Path} for patch group {group.PatchFile} directly: {ex.Message}", ex);
            }

            progress?.Report(new UpdateProgress(UpdatePhase.Patching, dst.Size, dst.Size, i + 1, group.DstFiles.Count, dst.Path));
        }
    }

    /// <summary>组幂等检测：该组全部 dstFiles 过 size+MD5 校验即视为已应用（中断/失败后重入跳过）。
    /// 异步流式 MD5（2026-10-02 假死修复）：重入时全部已应用组合计可达数十 GiB，同步 MD5 会长期
    /// 占用调用线程且不可取消。</summary>
    private static async Task<bool> GroupAlreadyAppliedAsync(
        string installDir, PatchGroup group, CancellationToken cancellationToken)
    {
        foreach (var dst in group.DstFiles)
        {
            var target = ManifestVerifier.ResolveSafe(installDir, dst.Path);
            if (await ManifestVerifier.CheckFileAsync(target, dst, withMd5: true, cancellationToken).ConfigureAwait(false)
                != FileStatus.Ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 带备份的安全替换：任一文件替换失败时回滚本组全部已替换文件。
    /// 条目在落位 Move 之前登记——落位本身失败（如 Windows 杀软锁文件）时在途条目
    /// 也要还原，否则组内留下缺失文件而原内容孤悬在 backup 里，重试只能整包重下。
    /// 回滚尽力而为：单个文件回滚失败不吞掉其余文件的还原，失败项拼进错误消息。
    /// internal 供单测（经 InternalsVisibleTo）。
    /// </summary>
    internal static void ReplaceWithBackup(string installDir, PatchGroup group, string newDir, ILogger? logger = null)
    {
        var replaced = new List<(string Target, string Backup, bool HadOriginal)>();
        try
        {
            foreach (var dst in group.DstFiles)
            {
                var target = ManifestVerifier.ResolveSafe(installDir, dst.Path);
                var backup = target + BackupSuffix;
                var hadOriginal = File.Exists(target);

                if (hadOriginal)
                {
                    // 只读目标的 Move/Delete 在 Windows 抛 UnauthorizedAccessException 且重试永不自愈
                    //（同链路其余三处覆盖点都有只读防线，此处补齐；Linux rename 不受目标只读影响）
                    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                    File.Move(target, backup, overwrite: true);
                }

                replaced.Add((target, backup, hadOriginal));

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(SafeJoin(newDir, dst.Path), target);
            }
        }
        catch (Exception ex)
        {
            var rollbackErrors = new List<string>();
            foreach (var (target, backup, hadOriginal) in replaced)
            {
                try
                {
                    if (hadOriginal && File.Exists(backup))
                    {
                        // 覆盖在途条目（新文件未落位，target 缺失）与已落位条目两种情形
                        if (File.Exists(target))
                        {
                            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                            File.Delete(target);
                        }

                        File.Move(backup, target, overwrite: true);
                    }
                    else if (File.Exists(target))
                    {
                        File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                        File.Delete(target);
                    }
                }
                catch (Exception rollbackEx)
                {
                    rollbackErrors.Add($"{Path.GetFileName(target)}: {rollbackEx.Message}");
                }
            }

            var note = rollbackErrors.Count > 0
                ? $" (rollback incomplete: {string.Join("; ", rollbackErrors)})"
                : string.Empty;
            throw new UpdateException($"Failed to swap files, group rolled back{note} ({group.PatchFile}): {ex.Message}", ex);
        }

        // 备份清理尽力而为：走到这里文件替换已全部成功，删除失败（Windows 杀软恰好锁住
        // .yagl-bak）不得推翻已完成且校验通过的结果——残留留给下次重置清理（2026-09-20 复审修复）
        foreach (var (_, backup, _) in replaced)
        {
            TryDeleteBackup(backup, logger);
        }
    }

    /// <summary>
    /// 尽力删除单个已消费的差分产物（组成功后回收的补丁、已应用组跳过的补丁）：失败不抛，
    /// 返回是否删除成功——更新/续跑结果不得被清理动作推翻（与 <see cref="TryDeleteBackup"/>
    /// 同契约）。<see cref="FileUtilities"/> 只有整目录的 <c>TryDeleteDirectory</c>，没有单文件版本。
    /// </summary>
    private static bool TryDeleteFile(string path, ILogger? logger = null)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not delete consumed patch file {Path}; left in place", path);
            return false;
        }
    }

    /// <summary>
    /// 尽力删除单个更新备份文件：失败不抛（调用点的更新已成功），返回是否删除成功。
    /// internal 供单测（经 InternalsVisibleTo）。
    /// </summary>
    internal static bool TryDeleteBackup(string backup, ILogger? logger = null)
    {
        try
        {
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not delete update backup {Backup}; left in place", backup);
            return false;
        }
    }
}
