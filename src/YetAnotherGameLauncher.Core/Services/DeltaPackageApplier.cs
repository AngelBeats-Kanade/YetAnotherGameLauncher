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
/// 整文件直接落位 / verify-only 只核在盘）→ 删除官方废弃清单 → 清理载荷目录。
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
        EnsureApplicable(installDir, plan);

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

            if (file.Patches.Count > 0)
            {
                await ApplyDeltaAsync(installDir, payload, plan, file, relative, target, cancellationToken).ConfigureAwait(false);
                patched++;
            }
            else if (!string.IsNullOrEmpty(file.FullFilePayloadPath))
            {
                CopyFullFile(installDir, payload, plan, file, relative, target);
                copied++;
            }
            else
            {
                // verify-only：前置校验已确认在盘；尺寸也核一下（便宜且能抓住截断/串位，
                // 内容级核对待消费官方 verify_files.json，记为缺口）
                var existing = new FileInfo(target);
                if (file.Size > 0 && existing.Length != file.Size)
                {
                    throw new DeltaPackageNotApplicableException(
                        $"Delta update not applicable: unchanged file has an unexpected size ({file.Name}); use the full update.");
                }

                verified++;
            }

            produced.Add(relative);
        }

        var deleted = CleanupObsoleteFiles(installDir, payload, plan, produced, progress);
        if (Directory.Exists(payloadDir) && !FileUtilities.TryDeleteDirectory(payloadDir, logger))
        {
            // 删不掉（占用/权限）不能只留 Debug 痕迹：8.7 GiB 载荷静默留盘与本批的目的相反
            logger?.LogWarning("Delta payload staging could not be removed: {Path}", payloadDir);
        }

        logger?.LogInformation(
            "Delta payload applied: {Patched} patched, {Copied} copied, {Verified} verified, {Deleted} obsolete removed.",
            patched, copied, verified, deleted);
        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, total, total, null));
        return new DeltaApplyResult(patched, copied, verified, deleted);
    }

    /// <summary>
    /// 前置校验（便宜、不落盘）：每个差分节点的基文件在盘、每个 verify-only 节点在盘。
    /// 内容级核对在逐文件应用时进行（基文件 md5 与官方清单不符同样折算"不适用"）；
    /// 由于没有节点以其它节点的产物为基文件，"先应用一部分再失败"仍是可重试的安全状态。
    /// </summary>
    private static void EnsureApplicable(string installDir, DeltaPackagePlan plan)
    {
        foreach (var file in plan.Files)
        {
            // 清单路径先全部归一（穿越即硬错误，不是"不适用"）
            _ = NormalizeManifestPath(file.Name, plan.VfsBasePath);
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

        var delta = MultiPartZipEntryReader.TryReadContent(payload, source.PatchPath, plan.CdKey, logger)
                    ?? throw new DeltaPackageUnusableException(
                        $"Delta payload entry is unreadable: {source.PatchPath}; falling back to the full update.");
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
            await File.WriteAllBytesAsync(deltaPath, delta, cancellationToken).ConfigureAwait(false);
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
    /// 补丁工具执行（复审 P2-3 / 第六轮 P2-1 / 第七轮 P3-5，2026-10-17）：`UpdateException`
    /// （非零退出/超时/工具缺失）与 `Win32Exception`（进程起不来：TOCTOU 被删/坏 exe/wine 启动失败；
    /// 含 `SocketException` 这一子类的理论逃逸——供给下载器只折 `DownloadException`）都折算
    /// "载荷不可用"→ 编排回退整包（并按既有链清掉载荷暂存，是"重下载荷 + 整包"的代价，知情接受），
    /// 不再形成"同一版本过渡内重试恒失败"；整包路径不需要 hpatchz，回退对用户更好，
    /// 按 Unusable 语义记 Warning。取消不吞。
    /// </summary>
    private async Task ApplyPatchFileAsync(
        string deltaPath, string basePath, string outputPath, string relative, CancellationToken cancellationToken)
    {
        try
        {
            await patchApplier.ApplyFileAsync(deltaPath, basePath, outputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UpdateException or System.ComponentModel.Win32Exception)
        {
            throw new DeltaPackageUnusableException(
                $"Patch application failed for {relative}: {ex.Message}; falling back to the full update.", ex);
        }
    }

    /// <summary>整文件节点：从载荷读内容（AES 条目经 cd_key 解密）→ 核对 md5/尺寸 → 就地落位。</summary>
    private void CopyFullFile(
        string installDir,
        SplitVolumeStream payload,
        DeltaPackagePlan plan,
        DeltaPackageFile file,
        string relative,
        string target)
    {
        var content = MultiPartZipEntryReader.TryReadContent(payload, file.FullFilePayloadPath!, plan.CdKey, logger)
                      ?? throw new DeltaPackageUnusableException(
                          $"Delta payload entry is unreadable: {file.FullFilePayloadPath}; falling back to the full update.");
        EnsureSafeTarget(installDir, target, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var outputPath = target + ".yagl-new";
        try
        {
            FileUtilities.RemoveLinkedLeaf(outputPath, logger);
            File.WriteAllBytes(outputPath, content);
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
            var content = MultiPartZipEntryReader.TryReadContent(payload, name, plan.CdKey, logger)
                          ?? throw new DeltaPackageUnusableException(
                              $"Delta payload static entry is unreadable: {name}; falling back to the full update.");
            EnsureSafeTarget(installDir, target, relative); // F19 同族：链接目录不得写穿（写前即核）
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var outputPath = target + ".yagl-new";
            try
            {
                FileUtilities.RemoveLinkedLeaf(outputPath, logger);
                File.WriteAllBytes(outputPath, content);
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
