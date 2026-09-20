using System.Text.Json;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// 增量更新（两段式，与 wutheringwaves-cli-manager 的 predownload --apply 对齐）：
/// 1) Predownload：把差分包（krpdiff）与可直接下载的新文件放入 {installDir}/.yagl/predownload 暂存；
/// 2) Apply：逐组"复制旧文件 → hpatchz 合成 → MD5 校验 → .yagl-bak 备份替换"，
///    单组失败自动回滚该组，中断后可重新执行。
/// </summary>
public sealed class IncrementalUpdateService(
    IDownloader downloader,
    IPatchApplier patchApplier,
    ILogger? logger = null)
{
    /// <summary>预下载暂存目录名，挂在 {installDir}/.yagl 之下。</summary>
    public const string PredownloadDirName = "predownload";
    private const string PatchWorkDirName = "patchwork";
    private const string BackupSuffix = ".yagl-bak";


    /// <summary>某安装目录的预下载暂存目录（{installDir}/.yagl/predownload）。</summary>
    public static string PredownloadDir(string installDir) =>
        Path.Combine(installDir, LocalStateService.StateDirName, PredownloadDirName);

    /// <summary>重建预下载暂存目录（清掉上次中断的残留）。</summary>
    /// <param name="installDir">游戏安装目录。</param>
    /// <returns>新建好的暂存目录路径。</returns>
    public static string ResetStaging(string installDir)
    {
        var staging = PredownloadDir(installDir);
        // Windows 上残留文件被占用（游戏运行中/杀软扫描）时整树删除会抛异常并卡死本次预下载，
        // 尽力清理即可：残留文件随后会被同名覆盖或按暂存清单核对
        FileUtilities.TryDeleteDirectory(staging);
        Directory.CreateDirectory(staging);
        return staging;
    }

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

    /// <summary>阶段一：把差分包与新文件下载到暂存目录（MD5/大小校验），并写入暂存清单供 Apply 使用。</summary>
    public async Task PredownloadAsync(
        string installDir,
        GameManifest incrementalManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var staging = ResetStaging(installDir);

        var totalBytes = incrementalManifest.Files.Sum(f => f.Size)
                         + incrementalManifest.Groups.Sum(g => g.PatchSize);
        var totalItems = incrementalManifest.Files.Count + incrementalManifest.Groups.Count;
        var done = 0;
        var bytes = 0L;

        progress?.Report(new UpdateProgress(UpdatePhase.Downloading, totalBytes, 0, 0, totalItems, null));

        // 两类暂存条目（直下新文件 / 差分包）下载流程一致：同一本地函数处理并聚合进度
        // （下载为顺序执行，无并发访问，计数无需加锁）
        async Task DownloadStagedAsync(string url, long size, string? md5, string relativeTarget, string displayName)
        {
            var target = SafeJoin(staging, relativeTarget);
            await downloader.DownloadFileAsync(new DownloadRequest(url, target, size, md5), null, cancellationToken).ConfigureAwait(false);
            bytes += size;
            done++;
            progress?.Report(new UpdateProgress(UpdatePhase.Downloading, totalBytes, bytes, done, totalItems, displayName));
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
        progress?.Report(new UpdateProgress(UpdatePhase.Done, totalBytes, bytes, totalItems, totalItems, null));
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
    public async Task ApplyAsync(
        string installDir,
        GameManifest incrementalManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var staging = PredownloadDir(installDir);
        var workDir = PatchWorkDir(installDir);
        FileUtilities.TryDeleteDirectory(workDir, logger);

        var total = incrementalManifest.Groups.Count;
        for (var i = 0; i < total; i++)
        {
            var group = incrementalManifest.Groups[i];
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new UpdateProgress(UpdatePhase.Patching, group.PatchSize * (total - i), 0, i, total, group.PatchFile));

            if (GroupAlreadyApplied(installDir, group))
            {
                logger?.LogDebug("Target file of group {Patch} already present; skipped", group.PatchFile);
                continue;
            }

            var patchPath = SafeJoin(Path.Combine(staging, "patches"), group.PatchFile);
            if (!File.Exists(patchPath))
            {
                throw new UpdateException(
                    $"Patch {group.PatchFile} was not preloaded; run predownload first or use the full update.");
            }

            await ApplyGroupAsync(installDir, workDir, i, group, patchPath, cancellationToken).ConfigureAwait(false);
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

        // 暂存目录完成使命后清理（Windows 上残留被占用时尽力清理即可，残留留给下次重置）
        FileUtilities.TryDeleteDirectory(staging, logger);

        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, total, total, null));
    }

    private async Task ApplyGroupAsync(
        string installDir,
        string workDir,
        int index,
        PatchGroup group,
        string patchPath,
        CancellationToken cancellationToken)
    {
        var oldDir = Path.Combine(workDir, $"olddir-{index}");
        var newDir = Path.Combine(workDir, $"newdir-{index}");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);

        foreach (var src in group.SrcFiles)
        {
            var source = ManifestVerifier.ResolveSafe(installDir, src.Path);
            if (!File.Exists(source))
            {
                throw new UpdateException(
                    $"Source file {src.Path} required by patch group {group.PatchFile} is missing; use the full update.");
            }

            var copied = SafeJoin(oldDir, src.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
            File.Copy(source, copied, overwrite: true);
        }

        try
        {
            await patchApplier.ApplyAsync(patchPath, oldDir, newDir, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UpdateException($"Patch application failed ({group.PatchFile}): {ex.Message}", ex);
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

    private static bool GroupAlreadyApplied(string installDir, PatchGroup group) =>
        group.DstFiles.All(dst =>
        {
            var target = ManifestVerifier.ResolveSafe(installDir, dst.Path);
            return ManifestVerifier.CheckFile(target, dst, withMd5: true) == FileStatus.Ok;
        });

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
                            File.Delete(target);
                        }

                        File.Move(backup, target, overwrite: true);
                    }
                    else if (File.Exists(target))
                    {
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
