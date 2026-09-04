using System.Text.Json;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;
using Microsoft.Extensions.Logging;

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
    public const string PredownloadDirName = "predownload";
    public const string PatchWorkDirName = "patchwork";
    private const string BackupSuffix = ".yagl-bak";

    private readonly IDownloader _downloader = downloader;
    private readonly IPatchApplier _patchApplier = patchApplier;

    public static string PredownloadDir(string installDir) =>
        Path.Combine(installDir, LocalStateService.StateDirName, PredownloadDirName);

    private static string PatchWorkDir(string installDir) =>
        Path.Combine(installDir, LocalStateService.StateDirName, PatchWorkDirName);

    /// <summary>阶段一：把差分包与新文件下载到暂存目录（MD5/大小校验），并写入暂存清单供 Apply 使用。</summary>
    public async Task PredownloadAsync(
        string installDir,
        GameManifest incrementalManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var staging = PredownloadDir(installDir);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);

        var totalBytes = incrementalManifest.Files.Sum(f => f.Size)
                         + incrementalManifest.Groups.Sum(g => g.PatchSize);
        var totalItems = incrementalManifest.Files.Count + incrementalManifest.Groups.Count;
        var done = 0;
        var bytes = 0L;
        var gate = new object();

        progress?.Report(new UpdateProgress(UpdatePhase.Downloading, totalBytes, 0, 0, totalItems, null));

        foreach (var file in incrementalManifest.Files)
        {
            if (file.Url is null)
            {
                throw new UpdateException($"增量清单条目缺少下载地址：{file.Path}");
            }

            var target = Path.Combine(staging, "files", file.Path.Replace('/', Path.DirectorySeparatorChar));
            await _downloader.DownloadFileAsync(
                new DownloadRequest(file.Url, target, file.Size, file.Md5), null, cancellationToken);
            lock (gate)
            {
                bytes += file.Size;
                done++;
            }
            progress?.Report(new UpdateProgress(UpdatePhase.Downloading, totalBytes, bytes, done, totalItems, file.Path));
        }

        foreach (var group in incrementalManifest.Groups)
        {
            if (group.Url is null)
            {
                throw new UpdateException($"差分包缺少下载地址：{group.PatchFile}");
            }

            var target = Path.Combine(staging, "patches", group.PatchFile.Replace('/', Path.DirectorySeparatorChar));
            await _downloader.DownloadFileAsync(
                new DownloadRequest(group.Url, target, group.PatchSize, group.PatchMd5), null, cancellationToken);
            lock (gate)
            {
                bytes += group.PatchSize;
                done++;
            }
            progress?.Report(new UpdateProgress(UpdatePhase.Downloading, totalBytes, bytes, done, totalItems, group.PatchFile));
        }

        await File.WriteAllTextAsync(
            Path.Combine(staging, "manifest.json"),
            JsonSerializer.Serialize(incrementalManifest, Json.Default),
            cancellationToken);

        logger?.LogInformation("预下载完成：{Groups} 组差分，{Files} 个文件", incrementalManifest.Groups.Count, incrementalManifest.Files.Count);
        progress?.Report(new UpdateProgress(UpdatePhase.Done, totalBytes, bytes, totalItems, totalItems, null));
    }

    /// <summary>读取暂存清单；没有已预下载内容时返回 null。</summary>
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
        catch (JsonException)
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
        if (Directory.Exists(workDir))
        {
            Directory.Delete(workDir, recursive: true);
        }

        var total = incrementalManifest.Groups.Count;
        for (var i = 0; i < total; i++)
        {
            var group = incrementalManifest.Groups[i];
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new UpdateProgress(UpdatePhase.Patching, group.PatchSize * (total - i), 0, i, total, group.PatchFile));

            if (GroupAlreadyApplied(installDir, group))
            {
                logger?.LogDebug("组 {Patch} 的目标文件已就绪，跳过", group.PatchFile);
                continue;
            }

            var patchPath = Path.Combine(staging, "patches", group.PatchFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(patchPath))
            {
                throw new UpdateException(
                    $"差分包 {group.PatchFile} 未预下载。请先执行预下载，或改用全量更新。");
            }

            await ApplyGroupAsync(installDir, workDir, i, group, patchPath, cancellationToken);
        }

        // 差分组不覆盖的新文件已在预下载时暂存，此处落位；缺失的交给事后修复
        var stagedFilesDir = Path.Combine(staging, "files");
        foreach (var file in incrementalManifest.Files)
        {
            var target = ManifestVerifier.ResolveSafe(installDir, file.Path);
            if (ManifestVerifier.CheckFile(target, file, withMd5: true) == FileStatus.Ok)
            {
                continue;
            }

            var stagedFile = Path.Combine(stagedFilesDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(stagedFile))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(stagedFile, target, overwrite: true);
            logger?.LogDebug("暂存新文件落位：{Path}", file.Path);
        }

        // 暂存目录完成使命后清理
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

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
                    $"差分组 {group.PatchFile} 需要的源文件 {src.Path} 不存在，请改用全量更新。");
            }

            var copied = Path.Combine(oldDir, src.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
            File.Copy(source, copied, overwrite: true);
        }

        try
        {
            await _patchApplier.ApplyAsync(patchPath, oldDir, newDir, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UpdateException($"补丁应用失败（{group.PatchFile}）：{ex.Message}", ex);
        }

        foreach (var dst in group.DstFiles)
        {
            var produced = Path.Combine(newDir, dst.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(produced))
            {
                throw new UpdateException($"补丁器未生成 {dst.Path}，差分组 {group.PatchFile} 应用失败。");
            }

            var producedLength = new FileInfo(produced).Length;
            if (producedLength != dst.Size
                || !string.Equals(Hashing.Md5Hex(produced), dst.Md5, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException($"补丁器生成的 {dst.Path} 校验失败，差分组 {group.PatchFile} 应用失败。");
            }
        }

        ReplaceWithBackup(installDir, group, newDir);
    }

    private static bool GroupAlreadyApplied(string installDir, PatchGroup group) =>
        group.DstFiles.All(dst =>
        {
            var target = ManifestVerifier.ResolveSafe(installDir, dst.Path);
            return ManifestVerifier.CheckFile(target, dst, withMd5: true) == FileStatus.Ok;
        });

    /// <summary>带备份的安全替换：任一文件替换失败时回滚本组全部已替换文件。</summary>
    private static void ReplaceWithBackup(string installDir, PatchGroup group, string newDir)
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

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(Path.Combine(newDir, dst.Path.Replace('/', Path.DirectorySeparatorChar)), target);
                replaced.Add((target, backup, hadOriginal));
            }
        }
        catch (Exception ex)
        {
            foreach (var (target, backup, hadOriginal) in replaced)
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                if (hadOriginal && File.Exists(backup))
                {
                    File.Move(backup, target, overwrite: true);
                }
            }

            throw new UpdateException($"替换文件失败，本组已回滚（{group.PatchFile}）：{ex.Message}", ex);
        }

        foreach (var (_, backup, _) in replaced)
        {
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }
        }
    }
}
