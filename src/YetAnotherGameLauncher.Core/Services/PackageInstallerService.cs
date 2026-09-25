using System.IO.Compression;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// 包式渠道安装器（终末地等整包分发的游戏）：
/// 下载压缩包 → size/MD5 校验 → 解压进安装目录 → 清理。
/// 支持两段式：Predownload 暂存压缩包，Apply 解压落盘。
/// </summary>
public sealed class PackageInstallerService(IDownloader downloader, ILogger? logger = null)
{

    /// <summary>两段式预下载的压缩包暂存目录（位于 .yagl/predownload/packages，Apply 时就地解压）。</summary>
    public static string PackagesDir(string installDir) => Path.Combine(
        installDir, LocalStateService.StateDirName, IncrementalUpdateService.PredownloadDirName, "packages");

    /// <summary>下载并解压全部压缩包（完整安装/更新）。</summary>
    public async Task InstallAsync(
        string installDir,
        GameManifest packageManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var packagesDir = Path.Combine(installDir, LocalStateService.StateDirName, "packages");
        var staged = await DownloadPackagesAsync(packagesDir, packageManifest, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new UpdateProgress(UpdatePhase.Patching, 0, 0, packageManifest.Files.Count, packageManifest.Files.Count, null));
        foreach (var (manifestFile, archivePath) in staged)
        {
            ExtractArchive(archivePath, installDir, manifestFile.Path);
            // 刚写完的 zip 可能正被杀软扫描锁定（Windows）：删不掉就留给下方目录清理重试，别打断安装
            FileUtilities.DeleteQuiet(archivePath);
        }

        // Windows 上残留包被占用时整树删除会抛异常，尽力清理即可
        FileUtilities.TryDeleteDirectory(packagesDir, logger);
        logger?.LogInformation("Package install finished: {Version} ({Count} archives)", packageManifest.Version, packageManifest.Files.Count);
        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, packageManifest.Files.Count, packageManifest.Files.Count, null));
    }

    /// <summary>预下载：把压缩包下载到暂存目录并写入暂存清单。已完整暂存（size/MD5 一致）
    /// 的包直接复用、不再下载：暂存清单写入失败或进程中断后重试是幂等的，不会把数十 GB
    /// 已下载内容全部作废（2026-09-20 复审修复；此前每次 Predownload 先整删暂存目录再全量重下）。</summary>
    public async Task PredownloadAsync(
        string installDir,
        GameManifest packageManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 不再整删暂存目录：中断残留由 IsArchiveIntact 核对消化，清单外的游离包单独清理
        var staging = IncrementalUpdateService.PredownloadDir(installDir);
        Directory.CreateDirectory(PackagesDir(installDir));

        await DownloadPackagesAsync(PackagesDir(installDir), packageManifest, progress, cancellationToken).ConfigureAwait(false);
        PruneForeignPackages(PackagesDir(installDir), packageManifest);

        await IncrementalUpdateService.WriteStagedManifestAsync(staging, packageManifest, cancellationToken).ConfigureAwait(false);

        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, packageManifest.Files.Count, packageManifest.Files.Count, null));
    }

    /// <summary>暂存路径比较策略（F20）：keep 集合与 seenTargets 必须同策略——Windows 文件名
    /// 不分大小写用 OrdinalIgnoreCase，Linux（Ordinal）按精确名。两处策略分叉曾让 Linux 上
    /// 两代清单间仅大小写变化的旧包永不清理（磁盘滞留）。</summary>
    private static StringComparer StagedPathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>清理暂存包目录里不属于当前清单的游离文件（上次预下载的旧代际残留）。</summary>
    internal static void PruneForeignPackages(string packagesDir, GameManifest packageManifest)
    {
        var keep = packageManifest.Files
            .Select(f => Path.GetFullPath(StagedArchivePath(packagesDir, f)))
            .ToHashSet(StagedPathComparer);
        foreach (var file in Directory.EnumerateFiles(packagesDir))
        {
            if (!keep.Contains(Path.GetFullPath(file)))
            {
                FileUtilities.DeleteQuiet(file);
            }
        }
    }

    /// <summary>
    /// 应用预下载：直接解压暂存压缩包（Predownload 的产物），完成后清理暂存目录。
    /// 正常情况下零下载；仅当某个包缺失或校验不通过（如暂存被中断/篡改）时才回退重新下载该包。
    /// </summary>
    public async Task ApplyPredownloadAsync(
        string installDir,
        GameManifest packageManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var staging = IncrementalUpdateService.PredownloadDir(installDir);
        var stagedDir = PackagesDir(installDir);
        var total = packageManifest.Files.Count;

        progress?.Report(new UpdateProgress(UpdatePhase.Patching, 0, 0, 0, total, null));
        foreach (var (file, index) in packageManifest.Files.Select((f, i) => (f, i)))
        {
            ManifestChecks.EnsureDownloadUrl(file.Url, "Package entry", file.Path);

            var archivePath = StagedArchivePath(stagedDir, file);
            if (!IsArchiveIntact(archivePath, file))
            {
                logger?.LogInformation("Staged package missing or corrupt, re-downloading: {Path}", file.Path);
                await downloader.DownloadFileAsync(
                    new DownloadRequest(file.Url, archivePath, file.Size, file.Md5), null, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new UpdateProgress(UpdatePhase.Patching, 0, 0, index, total, file.Path));
            ExtractArchive(archivePath, installDir, file.Path);
        }

        if (Directory.Exists(staging))
        {
            FileUtilities.TryDeleteDirectory(staging, logger);
        }

        logger?.LogInformation("Predownload applied: {Version} ({Count} archives)", packageManifest.Version, total);
        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, total, total, null));
    }

    /// <summary>压缩包在包目录内的落盘路径（清单里的反斜杠路径取末段文件名）。</summary>
    private static string StagedArchivePath(string packagesDir, ManifestFile file) =>
        Path.Combine(packagesDir, Path.GetFileName(file.Path.Replace('\\', '/')));

    /// <summary>暂存包完整性：文件存在、大小与 MD5 均与清单一致。size ≤ 0 或 MD5 为空表示
    /// 渠道未提供该字段的校验信息，只按存在性视为完好（与 <see cref="HttpFileDownloader.Verify"/>
    /// 同一语义）——否则字段缺失的暂存包恒判损坏，每次应用都整包重下（2026-09-20 复审补齐）。</summary>
    private static bool IsArchiveIntact(string archivePath, ManifestFile file) =>
        File.Exists(archivePath)
        && (file.Size <= 0 || new FileInfo(archivePath).Length == file.Size)
        && (string.IsNullOrEmpty(file.Md5)
            || Hashing.Md5Hex(archivePath).Equals(file.Md5, StringComparison.OrdinalIgnoreCase));

    private async Task<List<(ManifestFile File, string ArchivePath)>> DownloadPackagesAsync(
        string packagesDir,
        GameManifest packageManifest,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(packagesDir);
        var totalBytes = packageManifest.Files.Sum(f => f.Size);
        var downloaded = 0L;
        var staged = new List<(ManifestFile, string)>();
        var seenTargets = new HashSet<string>(StagedPathComparer);

        foreach (var (file, index) in packageManifest.Files.Select((f, i) => (f, i)))
        {
            ManifestChecks.EnsureDownloadUrl(file.Url, "Package entry", file.Path);

            var archivePath = StagedArchivePath(packagesDir, file);
            // 暂存落盘按清单路径末段命名：两个条目同名（不同 URL 目录）会互踩——后包覆盖先包，
            // 解压循环把同一文件解两次、先包内容从未落地且全程无错误（2026-09-20 复审修复）
            if (!seenTargets.Add(Path.GetFullPath(archivePath)))
            {
                throw new UpdateException(
                    $"Two package entries share the staged file name \"{file.Path}\"; refusing to silently overwrite one with the other.");
            }

            progress?.Report(new UpdateProgress(UpdatePhase.Downloading, totalBytes, downloaded, index, packageManifest.Files.Count, file.Path));

            if (!IsArchiveIntact(archivePath, file))
            {
                await downloader.DownloadFileAsync(
                    new DownloadRequest(file.Url, archivePath, file.Size, file.Md5), null, cancellationToken).ConfigureAwait(false);
            }

            downloaded += file.Size;
            staged.Add((file, archivePath));
        }

        return staged;
    }

    internal static void ExtractArchive(string archivePath, string installDir, string displayName)
    {
        try
        {
            // 不用 ZipFile.ExtractToDirectory：它在 Unix 上把含 '\' 的条目名当字面文件名
            // （dotnet/runtime#98247），Windows 打包器产出的包会在 Linux 解成安装根目录下的
            // 一堆平铺垃圾文件。手动遍历统一归一 '/'，顺带做沙箱校验与只读属性处理。
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                // 目录条目（null）由 ResolveEntryTarget 校验后就地建目录；
                // 调用方不得再用未清洗的原始条目名拼接，否则 rooted 名经 Path.Combine 逃出沙箱
                var target = ResolveEntryTarget(installDir, entry.FullName);
                if (target is null)
                {
                    continue;
                }

                // 既有目录符号链接会让词法沙箱失效：CreateDirectory 对链接目录是 no-op，
                // ExtractToFile 经链接写出沙箱（F19——词法防穿越管不住 reparse 点）
                EnsureNoReparseWithin(installDir, target);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    // 只读属性会让 overwrite 失败（Windows），就地解除
                    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                }

                entry.ExtractToFile(target, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"Failed to extract {displayName}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 解压条目落点：归一 '\'→'/'、去首部 '/'，拒绝 ".." 段与盘符根（清单不可信，防穿越——
    /// 目录条目与文件条目一视同仁）；目录条目（含空名）在校验通过后就地建目录并返回 null，
    /// 普通条目返回安装目录内的绝对路径。所有路径拼接只使用校验后的分段。
    /// </summary>
    private static string? ResolveEntryTarget(string installDir, string entryName)
    {
        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/').Where(p => p.Length > 0).ToArray();
        if (Path.IsPathRooted(normalized) || segments.Contains("..", StringComparer.Ordinal))
        {
            throw new IOException($"Archive entry escapes sandbox: {entryName}");
        }

        if (segments.Length == 0 || normalized.EndsWith('/'))
        {
            var dirTarget = Path.Combine([installDir, .. segments]);
            EnsureNoReparseWithin(installDir, dirTarget); // 目录条目同样不得穿过既有链接（F19）
            Directory.CreateDirectory(dirTarget);
            return null;
        }

        return Path.Combine([installDir, .. segments]);
    }

    /// <summary>
    /// 确保落点的父目录链（安装根之下）不含既有符号链接/reparse 点（F19）：清单不可信威胁
    /// 模型下，游戏自带或搬盘手法留下的目录链接会让解压产物落到沙箱之外。命中即抛
    /// IOException（由 ExtractArchive 统一折算 UpdateException）。
    /// </summary>
    private static void EnsureNoReparseWithin(string installDir, string target)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        var fullTarget = Path.GetFullPath(target);
        if (!fullTarget.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(fullTarget, root, StringComparison.Ordinal))
        {
            throw new IOException($"Archive target escapes sandbox: {target}");
        }

        var current = Path.GetDirectoryName(fullTarget);
        while (current is not null
               && !string.Equals(current, root, StringComparison.Ordinal)
               && current.Length > root.Length)
        {
            if (Directory.Exists(current) && FileUtilities.IsReparsePoint(current))
            {
                throw new IOException($"Archive target traverses a symlinked directory: {current}");
            }

            current = Path.GetDirectoryName(current);
        }
    }
}
