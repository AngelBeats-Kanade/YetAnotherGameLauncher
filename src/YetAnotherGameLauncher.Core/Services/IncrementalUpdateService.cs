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
///    该组，中断后可重新执行），随后落位暂存新文件、删除清单废弃文件（deleteFiles 在组后——
///    废弃文件可能同时是组差分源，2026-10-02 真机 P1 实锤）、清理暂存；差分源缺失或补丁器
///    执行失败时按注入的解析缝直下该组产物自救（组级回退）。
/// </summary>
public sealed class IncrementalUpdateService(
    IDownloader downloader,
    IPatchApplier patchApplier,
    ILogger? logger = null,
    TimeSpan? progressReportInterval = null)
{
    /// <summary>预下载暂存目录名，挂在 {installDir}/.yagl 之下。</summary>
    public const string PredownloadDirName = "predownload";
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
            Path.Combine(staging, "manifest.json"),
            JsonSerializer.Serialize(manifest, Json.Default),
            cancellationToken).ConfigureAwait(false);
    }

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
        var path = Path.Combine(PredownloadDir(installDir), "manifest.json");
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
    /// 差分源缺失（官方清单 deleteFiles ∩ srcFiles 交叉或安装被外力破坏）或补丁器执行失败时
    /// 按组直下产物自救；null = 不启用回退（按可操作报错收尾）。</param>
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
                continue;
            }

            progress?.Report(new UpdateProgress(UpdatePhase.Patching, group.PatchSize * (total - i), 0, i, total, group.PatchFile));

            var patchPath = SafeJoin(Path.Combine(staging, "patches"), group.PatchFile);
            if (!File.Exists(patchPath))
            {
                throw new UpdateException(
                    $"Patch {group.PatchFile} was not preloaded; run predownload first or use the full update.");
            }

            await ApplyGroupAsync(installDir, workDir, i, group, patchPath, dstUrlResolver, progress, cancellationToken).ConfigureAwait(false);
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

        // 暂存目录完成使命后清理（Windows 上残留被占用时尽力清理即可，残留留给下次重置）
        FileUtilities.TryDeleteDirectory(staging, logger);
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
        var producedThisUpdate = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            producedThisUpdate.Add(file.Path);
        }

        foreach (var group in manifest.Groups)
        {
            foreach (var dst in group.DstFiles)
            {
                producedThisUpdate.Add(dst.Path);
            }
        }

        foreach (var relative in manifest.DeleteFiles)
        {
            // 逃逸路径由 ResolveSafe 直接抛 UpdateException（2026-10-02 三轮统一，F42 同族）
            var target = ManifestVerifier.ResolveSafe(installDir, relative);

            if (producedThisUpdate.Contains(relative))
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
        var oldDir = Path.Combine(workDir, $"olddir-{index}");
        var newDir = Path.Combine(workDir, $"newdir-{index}");
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
                logger?.LogWarning(ex, "Patch application failed for {Patch}; falling back to direct downloads", group.PatchFile);

                if (dstUrlResolver is null)
                {
                    throw new UpdateException($"Patch application failed ({group.PatchFile}): {ex.Message}", ex);
                }

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
