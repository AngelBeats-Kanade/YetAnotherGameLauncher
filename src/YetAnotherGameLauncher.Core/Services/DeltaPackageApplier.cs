using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>增量载荷应用的结果计数。</summary>
public sealed record DeltaApplyResult(int Patched, int Copied, int Verified, int Deleted);

/// <summary>
/// 包式渠道 v2 增量更新的应用器（终末地实测形态，2026-10-17）：
/// 下载 9 卷载荷（≈8.72 GiB，对比整包 58 GiB）→ 逐文件按官方清单合成（差分经 hpatchz /
/// 整文件直接落位 / verify-only 核对在盘（尺寸 + 内容 md5））→ 删除官方废弃清单。
/// 幂等续跑（2026-10-19 三审）：已到位节点（尺寸 + md5 与官方清单一致）直接跳过——应用期掉线/暂停
/// 后重进本方法零下载收敛（差分节点以基 md5 为判据，内容已应用再走一遍会被判"不适用"而错误回退
/// 整包重下）。载荷分卷由调用方在提交点（状态落盘之后）清理，本方法不自删（失败/取消保留供复用）。
/// 关键事实（真机实证）：差分节点与目标**零重叠**（224/224），因此可就地逐文件替换，
/// 不需要数十 GiB 的暂存区；应用中途失败保持可重试（旧文件只在被替换时丢弃，且没有别的
/// 节点依赖它）。
/// 失败折算：安装不适用（基文件/只读节点问题）抛 <see cref="DeltaPackageNotApplicableException"/>，
/// 载荷自身不可用（条目读不出、产出与清单不符）抛 <see cref="DeltaPackageUnusableException"/>，
/// 两者都由编排回退整包（2026-10-17 复审：否则载荷侧异常会让用户重试恒失败）；
/// 本地 IO/路径越界才算真错误。
/// </summary>
public sealed class DeltaPackageApplier(IDownloader downloader, IFilePatchApplier patchApplier, ILogger? logger = null)
{
    /// <summary>载荷分卷的暂存目录（位于 .yagl/delta；重跑按 size+MD5 复用）。</summary>
    public static string PayloadDir(string installDir) => Path.Combine(
        installDir, LocalStateService.StateDirName, "delta");

    /// <summary>载荷内官方废弃清单的条目名。</summary>
    internal const string ObsoleteListEntryName = "delete_files.txt";

    /// <summary>
    /// 应用增量载荷到 <paramref name="installDir"/>。前置校验（基文件/只读节点在盘）在下载前执行，
    /// 不通过抛 <see cref="DeltaPackageNotApplicableException"/> 由调用方回退整包。
    /// </summary>
    public async Task<DeltaApplyResult> ApplyAsync(
        string installDir,
        DeltaPackagePlan plan,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var payloadDir = PayloadDir(installDir);
        var alreadyAtTarget = EnsureApplicable(installDir, plan);
        EnsureEnoughFreeSpace(installDir, payloadDir, plan);

        var volumes = await DownloadVolumesAsync(payloadDir, plan, progress, cancellationToken).ConfigureAwait(false);
        var total = plan.Files.Count;
        var patched = 0;
        var copied = 0;
        var verified = 0;

        using var payload = new SplitVolumeStream(volumes);
        var produced = CopyStaticEntries(installDir, payload, plan, progress, cancellationToken);
        for (var index = 0; index < total; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = plan.Files[index];
            var relative = NormalizeManifestPath(file.Name, plan.VfsBasePath);
            var target = Path.Combine([installDir, .. relative.Split('/')]);
            progress?.Report(new UpdateProgress(UpdatePhase.Patching, 0, 0, index, total, file.Name));

            // 幂等续跑（2026-10-19 三审）：预检已判定的"已到位"节点在此放行（每个节点只算一次
            // md5，大文件重跑不重复整读）；verify-only 节点走下方既有核对路径，那里本来就是
            // 尺寸 + md5 的幂等语义
            if (alreadyAtTarget.Contains(relative))
            {
                logger?.LogDebug("Delta entry already at target: {Entry}", relative);
                verified++;
                produced.Add(relative);
                continue;
            }

            if (file.Patches.Count > 0)
            {
                await ApplyDeltaAsync(installDir, payload, plan, file, relative, target, cancellationToken).ConfigureAwait(false);
                patched++;
            }
            else if (!string.IsNullOrEmpty(file.FullFilePayloadPath))
            {
                CopyFullFile(installDir, payload, plan, file, relative, target, cancellationToken);
                copied++;
            }
            else
            {
                // verify-only：前置校验已确认在盘；尺寸 + **内容 md5** 都在此核对
                await VerifyUnchangedAsync(target, file, index, total, progress, cancellationToken).ConfigureAwait(false);
                verified++;
            }

            produced.Add(relative);
        }

        var deleted = CleanupObsoleteFiles(installDir, payload, plan, produced, progress);
        // **载荷清理移出本方法**（2026-10-19 三审）：应用成功到状态落盘之间（同步的废弃清单清理 +
        // 调用方写盘）是真实可达的暂停/掉线窗口，载荷（9 卷 ≈8.72 GiB）必须在场，续跑才能按
        // "已到位跳过"零下载收敛。清理由调用方在提交点负责：直链 = UpdateAsync 状态落盘后；
        // 预下载链 = ApplyPredownloadAsync 提交后 ClearStagedDelta。失败/取消照旧保留载荷供复用。
        // 句柄必须先释放再交还：提交点的整树删除发生在分卷句柄关闭之后（Windows 删除要求
        // FILE_SHARE_DELETE，而 SplitVolumeStream 以 FileShare.Read 打开分卷；Linux 的 unlink
        // 语义掩盖该差异，2026-10-19 CI Windows 腿两例红即此）；using 兜底重复释放，Dispose 幂等。
        payload.Dispose();

        logger?.LogInformation(
            "Delta payload applied: {Patched} patched, {Copied} copied, {Verified} verified, {Deleted} obsolete removed.",
            patched, copied, verified, deleted);
        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, total, total, null));
        return new DeltaApplyResult(patched, copied, verified, deleted);
    }

    /// <summary>
    /// 前置校验（便宜、不落盘）：每个差分节点的基文件在盘、每个 verify-only 节点在盘。
    /// 基文件 md5 与"仅校验"节点的内容 md5 都在逐文件应用时核对（不符同样折算"不适用"）；
    /// 由于没有节点以其它节点的产物为基文件，"先应用一部分再失败"仍是可重试的安全状态。
    /// 例外（2026-10-19 三审）：节点已到位（尺寸 + md5 与清单一致）时不再要求基文件在盘——
    /// "应用已完成、上一轮的官方废弃清理已删掉旧基文件"的续跑形态必须能放行，否则会被判
    /// "不适用"而整包重下。
    /// </summary>
    /// <returns>已到位节点（归一后的相对路径）：应用循环按此跳过，避免同一节点重复整读算 md5。</returns>
    private static HashSet<string> EnsureApplicable(string installDir, DeltaPackagePlan plan)
    {
        var alreadyAtTarget = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in plan.Files)
        {
            // 清单路径先全部归一（穿越即硬错误，不是"不适用"）
            var relative = NormalizeManifestPath(file.Name, plan.VfsBasePath);
            if ((file.Patches.Count > 0 || !string.IsNullOrEmpty(file.FullFilePayloadPath))
                && AlreadyAtTarget(Path.Combine(installDir, ToLocalPath(relative)), file))
            {
                alreadyAtTarget.Add(relative);
                continue;
            }

            if (file.Patches.Count > 0)
            {

                // 官方允许多候选（一条目标多个基文件变体）：至少一条候选的基文件在盘才可能应用；
                // 候选路径全部先归一并校验（穿越是硬错误），命中与否留到应用时按 md5 决定
                var candidatePaths = new List<string>(file.Patches.Count);
                foreach (var patch in file.Patches)
                {
                    candidatePaths.Add(Path.Combine(installDir, ToLocalPath(
                        NormalizeManifestPath(patch.BaseFile, plan.VfsBasePath))));
                }

                if (!candidatePaths.Any(File.Exists))
                {
                    throw new DeltaPackageNotApplicableException(
                        $"Delta update not applicable: base file is missing " +
                        $"({string.Join(", ", file.Patches.Select(p => p.BaseFile))}); use the full update.");
                }

                continue;
            }

            if (file.FullFilePayloadPath is null)
            {
                var target = Path.Combine(
                    installDir, ToLocalPath(NormalizeManifestPath(file.Name, plan.VfsBasePath)));
                if (!File.Exists(target))
                {
                    throw new DeltaPackageNotApplicableException(
                        $"Delta update not applicable: unchanged file is missing ({file.Name}); use the full update.");
                }
            }
        }

        return alreadyAtTarget;
    }

    /// <summary>
    /// 空闲空间预检（2026-10-17 立案③：增量补完所需空间 = 缺失新内容 + 载荷，实测曾超过当期空闲）：
    /// 需求 = 载荷全量（下载全程占盘）+ 各载荷节点的净增长（新尺寸 − 现存尺寸，缺失按 0 计）
    /// + 最大单节点尺寸（产出临时文件与旧文件并存的一次性瞬态）。
    /// 探不到空闲值（平台/权限）直接放行——预检是防呆，不能变成新的阻断源；
    /// 空间不足抛可操作错误，且**不回退整包**（整包只会要得更多）。
    /// </summary>
    private static void EnsureEnoughFreeSpace(string installDir, string payloadDir, DeltaPackagePlan plan)
    {
        var payloadBytes = plan.Volumes.Sum(v => v.Size);
        long growth = 0;
        long largest = 0;
        foreach (var file in plan.Files)
        {
            if (file.Patches.Count == 0 && string.IsNullOrEmpty(file.FullFilePayloadPath))
            {
                continue; // 仅校验节点不产出内容
            }

            var target = Path.Combine(
                installDir, ToLocalPath(NormalizeManifestPath(file.Name, plan.VfsBasePath)));
            var existing = File.Exists(target) ? new FileInfo(target).Length : 0;
            growth += Math.Max(0, file.Size - existing);
            largest = Math.Max(largest, file.Size);
        }

        var required = payloadBytes + growth + largest;
        var probes = new[] { DiskSpace.TryGetAvailableFreeBytes(payloadDir), DiskSpace.TryGetAvailableFreeBytes(installDir) }
            .Where(free => free.HasValue)
            .Select(free => free!.Value)
            .ToArray();
        if (probes.Length == 0 || probes.Min() >= required)
        {
            return;
        }

        var available = probes.Min();
        throw new UpdateException(
            $"Not enough free disk space for the delta update on the target volume: " +
            $"{required / (double)(1L << 30):F2} GiB required (payload {payloadBytes / (double)(1L << 30):F2} GiB " +
            $"+ produced content), {available / (double)(1L << 30):F2} GiB available. " +
            "Free up space and retry - a previously downloaded payload is reused.");
    }

    /// <summary>
    /// "仅校验"节点的在位核对：尺寸 + **内容 md5**（2026-10-18 立案补齐）。官方 <c>verify_files.json</c>
    /// 是二进制加密块、社区参考实现也不用它——增量清单自带的目标 md5 是唯一可用判据。尺寸相同但内容
    /// 损坏/被换过的文件在这里暴露，折算"安装不适用"回退整包（该节点没有载荷，增量无法自愈它）。
    /// 读取为流式异步（可取消、逐块上报 Verifying 进度帧）：1.6.4 计划里 717 条、可达数十 GiB，
    /// 不能同步阻塞调用线程。
    /// </summary>
    private static async Task VerifyUnchangedAsync(
        string target,
        DeltaPackageFile file,
        int index,
        int total,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (file.Size > 0 && new FileInfo(target).Length != file.Size)
        {
            throw new DeltaPackageNotApplicableException(
                $"Delta update not applicable: unchanged file has an unexpected size ({file.Name}); use the full update.");
        }

        if (string.IsNullOrEmpty(file.Md5))
        {
            return; // 清单未给 md5（防御）：只能按在位处理
        }

        progress?.Report(new UpdateProgress(UpdatePhase.Verifying, 0, 0, index, total, file.Name));
        var actual = await Hashing.Md5HexAsync(target, cancellationToken).ConfigureAwait(false);
        if (!actual.Equals(file.Md5, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeltaPackageNotApplicableException(
                $"Delta update not applicable: unchanged file content differs from the official manifest ({file.Name}); " +
                "use the full update.");
        }
    }

    /// <summary>
    /// 只下载（并核验）载荷分卷到 <c>.yagl/delta</c>，不应用——预下载链用（2026-10-18 接线）。
    /// 与 <see cref="ApplyAsync"/> 共用下载与空间预检逻辑；返回载荷总字节数。
    /// </summary>
    public async Task<long> StagePayloadAsync(
        string installDir,
        DeltaPackagePlan plan,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var payloadDir = PayloadDir(installDir);
        EnsureEnoughFreeSpace(installDir, payloadDir, plan);
        await DownloadVolumesAsync(payloadDir, plan, progress, cancellationToken).ConfigureAwait(false);
        return plan.Volumes.Sum(volume => volume.Size);
    }

    /// <summary>下载全部分卷（完整暂存则跳过；字节级进度经聚合器节流投递）。</summary>
    private async Task<IReadOnlyList<string>> DownloadVolumesAsync(
        string payloadDir, DeltaPackagePlan plan, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(payloadDir);
        var totalBytes = plan.Volumes.Sum(v => v.Size);
        var staged = new List<string>(plan.Volumes.Count);
        var aggregator = new ByteProgressAggregator(
            TimeSpan.FromMilliseconds(100),
            bytes => progress?.Report(new UpdateProgress(
                UpdatePhase.Downloading, totalBytes, bytes, staged.Count, plan.Volumes.Count, null)));

        foreach (var volume in plan.Volumes)
        {
            var path = Path.Combine(payloadDir, StagedVolumeName(volume.Url));
            if (!IsVolumeIntact(path, volume))
            {
                var fileProgress = aggregator.CreateFileProgress();
                await downloader.DownloadFileAsync(
                    new DownloadRequest(volume.Url, path, volume.Size, volume.Md5), fileProgress, cancellationToken)
                    .ConfigureAwait(false);
                aggregator.Add(volume.Size - fileProgress.ConsumedBytes);
            }
            else
            {
                aggregator.Add(volume.Size);
            }

            aggregator.ForceReport();
            staged.Add(path);
        }

        return staged;
    }

    /// <summary>分卷完整性：存在 + 尺寸/MD5 与计划一致（缺校验信息的项按存在性，与下载器同语义）。</summary>
    private static bool IsVolumeIntact(string path, DeltaPackageVolume volume)
    {
        try
        {
            return File.Exists(path)
                   && (volume.Size <= 0 || new FileInfo(path).Length == volume.Size)
                   && (string.IsNullOrEmpty(volume.Md5)
                       || Hashing.Md5Hex(path).Equals(volume.Md5, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>节点是否已是目标内容（尺寸 + 内容 md5 与官方清单一致）：续跑时据此跳过已应用条目。
    /// 差分节点以基 md5 为判据，内容已应用的节点重走差分会被判"不适用"从而错误回退整包，必须在此放行。</summary>
    private static bool AlreadyAtTarget(string target, DeltaPackageFile file)
    {
        try
        {
            if (!File.Exists(target))
            {
                return false;
            }

            return (file.Size <= 0 || new FileInfo(target).Length == file.Size)
                   && !string.IsNullOrEmpty(file.Md5)
                   && Hashing.Md5Hex(target).Equals(file.Md5, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>合成单个差分节点：基文件 md5 核对 → hpatchz 产出临时文件 → 目标 md5/尺寸核对 → 就地替换。</summary>
    private async Task ApplyDeltaAsync(
        string installDir,
        SplitVolumeStream payload,
        DeltaPackagePlan plan,
        DeltaPackageFile file,
        string relative,
        string target,
        CancellationToken cancellationToken)
    {
        var (source, basePath) = SelectPatchSource(installDir, plan, file)
            ?? throw new DeltaPackageNotApplicableException(
                $"Delta update not applicable: base file content differs from the official manifest " +
                $"({string.Join(", ", file.Patches.Select(p => p.BaseFile))}); use the full update.");

        EnsureSafeTarget(installDir, target, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var deltaPath = target + ".yagl-delta";
        var outputPath = target + ".yagl-new";
        try
        {
            // 叶子复核（复审非阻塞 5）：父目录链之外，临时名本身也可能是既有链接——写前解除；
            // 放在 try 内让解除失败走下面的 IO 折算（响亮失败，不静默写穿）
            FileUtilities.RemoveLinkedLeaf(deltaPath, logger);
            FileUtilities.RemoveLinkedLeaf(outputPath, logger);
            // 流式落盘（2026-10-18 大条目内存峰值立案）：补丁载荷边解密边写，不在内存里成型；
            // 读不出/认证不符折算"载荷不可用"，半成品由 finally 删除
            using (var destination = new FileStream(deltaPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (!MultiPartZipEntryReader.TryCopyContentTo(
                        payload, source.PatchPath, plan.CdKey, destination, cancellationToken, logger))
                {
                    throw new DeltaPackageUnusableException(
                        $"Delta payload entry is unreadable: {source.PatchPath}; falling back to the full update.");
                }
            }

            await ApplyPatchFileAsync(deltaPath, basePath, outputPath, relative, cancellationToken).ConfigureAwait(false);
            VerifyProduced(outputPath, file, relative);
            Replace(target, outputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"Failed to write the delta-produced file {relative}: {ex.Message}", ex);
        }
        finally
        {
            FileUtilities.DeleteQuiet(deltaPath);
            FileUtilities.DeleteQuiet(outputPath);
        }
    }

    /// <summary>
    /// 选可用候选（官方一条目标可给多条候选，2026-10-17 实测 224/224 单候选，但形态要与实现一致）：
    /// 取第一个基文件在盘且 md5 与清单一致的候选；都不行返回 null（折算"不适用"回退整包）。
    /// </summary>
    private static (DeltaPatchSource Source, string BasePath)? SelectPatchSource(
        string installDir, DeltaPackagePlan plan, DeltaPackageFile file)
    {
        foreach (var candidate in file.Patches)
        {
            var basePath = Path.Combine(
                installDir, ToLocalPath(NormalizeManifestPath(candidate.BaseFile, plan.VfsBasePath)));
            if (!File.Exists(basePath))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(candidate.BaseMd5)
                && !Hashing.Md5Hex(basePath).Equals(candidate.BaseMd5, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return (candidate, basePath);
        }

        return null;
    }

    /// <summary>
    /// 补丁工具执行的三分类（2026-10-18，复审 P3-5 分家）：工具不可用/供给失败/进程起不来 →
    /// <see cref="DeltaPackageSupplyException"/>（载荷是好的，调用方保留载荷暂存供零重下重试）；
    /// 非零退出/超时（<see cref="UpdateException"/>）→ <see cref="DeltaPackageUnusableException"/>
    /// （补丁内容对不上，重试同一份计划不会自愈）。取消不吞。
    /// 两类的共同点：都不再形成"同一版本过渡内重试恒失败"的形态。
    /// </summary>
    private async Task ApplyPatchFileAsync(
        string deltaPath, string basePath, string outputPath, string relative, CancellationToken cancellationToken)
    {
        try
        {
            await patchApplier.ApplyFileAsync(deltaPath, basePath, outputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (PatchToolUnavailableException ex)
        {
            throw new DeltaPackageSupplyException($"Patch tool unavailable for {relative}: {ex.Message}", ex);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 补丁器实现直接抛 Win32Exception（未经 HpatchzApplier 折算）：进程起不来 = 工具/环境侧
            throw new DeltaPackageSupplyException($"Patch tool failed to start for {relative}: {ex.Message}", ex);
        }
        catch (UpdateException ex)
        {
            throw new DeltaPackageUnusableException(
                $"Patch application failed for {relative}: {ex.Message}; falling back to the full update.", ex);
        }
    }

    /// <summary>整文件节点：从载荷流式读出内容（AES 条目经 cd_key 解密）→ 核对 md5/尺寸 → 就地落位。
    /// 2026-10-18 起走流式通道：1.6.4 实测有 5 个 &gt;512 MiB 的整文件条目（最大 1199.9 MiB），
    /// 入内存成型约 2 倍峰值。</summary>
    private void CopyFullFile(
        string installDir,
        SplitVolumeStream payload,
        DeltaPackagePlan plan,
        DeltaPackageFile file,
        string relative,
        string target,
        CancellationToken cancellationToken)
    {
        EnsureSafeTarget(installDir, target, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var outputPath = target + ".yagl-new";
        try
        {
            FileUtilities.RemoveLinkedLeaf(outputPath, logger);
            using (var destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (!MultiPartZipEntryReader.TryCopyContentTo(
                        payload, file.FullFilePayloadPath!, plan.CdKey, destination, cancellationToken, logger))
                {
                    throw new DeltaPackageUnusableException(
                        $"Delta payload entry is unreadable: {file.FullFilePayloadPath}; falling back to the full update.");
                }
            }

            VerifyProduced(outputPath, file, relative);
            Replace(target, outputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"Failed to write the delta-produced file {relative}: {ex.Message}", ex);
        }
        finally
        {
            FileUtilities.DeleteQuiet(outputPath);
        }
    }

    /// <summary>F19 同族防线：落点父目录链不得含既有链接（载荷/清单不可信，链接会让写穿沙箱）。</summary>
    private static void EnsureSafeTarget(string installDir, string target, string relative)
    {
        try
        {
            FileUtilities.EnsureNoReparseWithin(installDir, target);
        }
        catch (IOException ex)
        {
            throw new UpdateException($"Delta target is not safe to write ({relative}): {ex.Message}", ex);
        }
    }

    /// <summary>产物核对：尺寸 + MD5 与官方清单一致（不一致即拒收，绝不落位坏内容；
    /// 判定为"载荷不可用"由编排回退整包——重试同一份计划不会变好）。</summary>
    private static void VerifyProduced(string path, DeltaPackageFile file, string relative)
    {
        var info = new FileInfo(path);
        if ((file.Size > 0 && info.Length != file.Size)
            || (!string.IsNullOrEmpty(file.Md5)
                && !Hashing.Md5Hex(path).Equals(file.Md5, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DeltaPackageUnusableException(
                $"Delta-produced file does not match the official manifest: {relative} (size {info.Length}/{file.Size}); " +
                "falling back to the full update.");
        }
    }

    /// <summary>就地替换：Windows 上先清只读属性，避免 overwrite 失败。</summary>
    private static void Replace(string target, string outputPath)
    {
        if (File.Exists(target))
        {
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        }

        File.Move(outputPath, target, overwrite: true);
    }

    /// <summary>
    /// 静态条目覆盖（终末地载荷形态，规则由渠道放进计划）：载荷里除 VFS 节点/差分/清单类之外的
    /// 条目是整文件（dll/exe/config.ini 等），直接覆盖游戏根；<c>config.ini</c> 按官方习惯写
    /// <c>config.ini.new</c>（由计划的改写表提供）。
    /// </summary>
    private List<string> CopyStaticEntries(
        string installDir,
        SplitVolumeStream payload,
        DeltaPackagePlan plan,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var applied = new List<string>();
        // 列举失败与"确实没有静态条目"必须区分（复审 P3-5）：前者意味着 dll/exe 不会更新，
        // 静默成功会留下半更新安装——折算"载荷不可用"回退整包
        var names = MultiPartZipEntryReader.TryListEntryNames(payload, logger)
                    ?? throw new DeltaPackageUnusableException(
                        "Delta payload central directory is unreadable; falling back to the full update.");
        var staticEntries = names.Where(name =>
                !name.EndsWith('/')
                && !plan.StaticEntryExcludedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))
                && !plan.StaticEntryExcludedNames.Contains(name, StringComparer.Ordinal))
            .ToArray();
        for (var index = 0; index < staticEntries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); // 大 dll 同步写期间也要能取消
            var name = staticEntries[index];
            progress?.Report(new UpdateProgress(UpdatePhase.Patching, 0, 0, index, staticEntries.Length, name));
            var targetRelative = plan.StaticEntryRewrites.TryGetValue(name, out var rewritten) ? rewritten : name;
            var relative = NormalizeManifestPath(targetRelative, string.Empty);
            var target = Path.Combine([installDir, .. relative.Split('/')]);
            // 逐条落点日志（review nit）：静态条目是"denylist 之外一律覆盖游戏根"，没有审计线索时
            // 协议日后新增的元数据条目会被静默写进游戏根
            logger?.LogInformation("Delta payload static entry: {Entry} -> {RelativePath}", name, relative);
            EnsureSafeTarget(installDir, target, relative); // F19 同族：链接目录不得写穿（写前即核）
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var outputPath = target + ".yagl-new";
            try
            {
                FileUtilities.RemoveLinkedLeaf(outputPath, logger);
                using (var destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (!MultiPartZipEntryReader.TryCopyContentTo(
                            payload, name, plan.CdKey, destination, cancellationToken, logger))
                    {
                        throw new DeltaPackageUnusableException(
                            $"Delta payload static entry is unreadable: {name}; falling back to the full update.");
                    }
                }

                Replace(target, outputPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UpdateException($"Failed to write the delta payload static entry {relative}: {ex.Message}", ex);
            }
            finally
            {
                FileUtilities.DeleteQuiet(outputPath);
            }

            applied.Add(relative);
        }

        logger?.LogInformation("Delta payload static entries applied: {Count}.", staticEntries.Length);
        return applied;
    }

    /// <summary>删除载荷内的官方废弃清单（复用包式安装的清理逻辑：产物优先、沙箱、状态目录不删）。</summary>
    private int CleanupObsoleteFiles(
        string installDir,
        SplitVolumeStream payload,
        DeltaPackagePlan plan,
        IReadOnlyCollection<string> produced,
        IProgress<UpdateProgress>? progress)
    {
        var content = MultiPartZipEntryReader.TryReadContent(payload, ObsoleteListEntryName, plan.CdKey, logger);
        if (content is null)
        {
            logger?.LogWarning(
                "Delta payload has no readable {Entry}; skipping obsolete file cleanup.", ObsoleteListEntryName);
            return 0;
        }

        var obsolete = System.Text.Encoding.UTF8.GetString(content)
            .Split('\n')
            .Select(line => line.Trim('\r', ' ', '\t'))
            .Where(line => line.Length > 0)
            .ToArray();
        return PackageInstallerService.DeleteObsoleteFiles(installDir, obsolete, produced, progress, logger);
    }

    /// <summary>清单路径归一（'/' 分隔、拒穿越）并拼到 VFS 基路径下（终末地清单的 name 相对 VFS 根；
    /// 静态条目传 <see cref="string.Empty"/> 表示相对游戏根）。</summary>
    internal static string NormalizeManifestPath(string name, string vfsBasePath)
    {
        var relative = name.Replace('\\', '/').TrimStart('/');
        var segments = relative.Split('/').Where(segment => segment.Length > 0).ToArray();
        if (Path.IsPathRooted(relative) || segments.Contains("..", StringComparer.Ordinal) || segments.Length == 0)
        {
            throw new UpdateException($"Delta manifest path escapes the install root: {name}");
        }

        var baseSegments = vfsBasePath.Replace('\\', '/').Split('/').Where(segment => segment.Length > 0);
        return string.Join('/', [.. baseSegments, .. segments]);
    }

    private static string ToLocalPath(string relative) =>
        relative.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>分卷暂存名：URL 末段（去 query）。</summary>
    private static string StagedVolumeName(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Segments.Length > 0
            ? Uri.UnescapeDataString(uri.Segments[^1].TrimEnd('/'))
            : $"volume-{Guid.NewGuid():N}";
}
