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

    /// <summary>下载并解压全部压缩包（完整安装/更新）。obsoleteFiles = 官方点名的废弃文件相对路径
    /// （渠道 GetObsoleteFilesAsync，可为 null）：解压完成后删除，回收上一版本的残留
    /// （终末地旧 VFS 分块实测 47.72 GiB）。</summary>
    public async Task InstallAsync(
        string installDir,
        GameManifest packageManifest,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? obsoleteFiles = null)
    {
        var packagesDir = Path.Combine(installDir, LocalStateService.StateDirName, "packages");
        var staged = await DownloadPackagesAsync(packagesDir, packageManifest, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new UpdateProgress(UpdatePhase.Patching, 0, 0, packageManifest.Files.Count, packageManifest.Files.Count, null));
        var produced = ExtractStagedArchives(staged, packageManifest.ArchivesAreSplitVolumes, installDir);
        DeleteObsoleteFiles(installDir, obsoleteFiles, produced, progress, logger);
        foreach (var (_, archivePath) in staged)
        {
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
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? obsoleteFiles = null)
    {
        // 旧版本写入的暂存清单没有 ArchivesAreSplitVolumes（字段缺省 = false）：分卷暂存会在
        // 应用时被逐卷当独立 zip 解压——正是"End of Central Directory record could not be found"
        // 的用户报障形态，而此刻几十 GiB 已经下载完。按同一分卷规则复算救回这段存量状态
        // （review 非阻塞 3，2026-10-17）
        var manifest = EnsureSplitVolumeManifest(packageManifest);
        var staging = IncrementalUpdateService.PredownloadDir(installDir);
        var stagedDir = PackagesDir(installDir);
        var total = manifest.Files.Count;

        progress?.Report(new UpdateProgress(UpdatePhase.Patching, 0, 0, 0, total, null));
        var staged = new List<(ManifestFile File, string ArchivePath)>(total);
        foreach (var (file, index) in manifest.Files.Select((f, i) => (f, i)))
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
            staged.Add((file, archivePath));
        }

        // 解压统一在全部条目就绪之后：分卷归档必须凑齐所有分卷才能拼成一个流
        var produced = ExtractStagedArchives(staged, manifest.ArchivesAreSplitVolumes, installDir);
        DeleteObsoleteFiles(installDir, obsoleteFiles, produced, progress, logger);

        if (Directory.Exists(staging))
        {
            FileUtilities.TryDeleteDirectory(staging, logger);
        }

        logger?.LogInformation("Predownload applied: {Version} ({Count} archives)", manifest.Version, total);
        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, total, total, null));
    }

    /// <summary>
    /// 旧暂存清单的分卷标记复算（review 非阻塞 3，2026-10-17）：字段缺失 = false 会让分卷暂存
    /// 退回逐卷解压（用户报障形态），而此刻下载已完成。按 <see cref="SplitVolumeNaming"/>
    /// （渠道共用同一规则）复算：Ordered → 认定分卷并重排（老清单的卷序可能是服务器响应顺序）；
    /// Broken → 明确报错（拼接结果必定不是合法归档，早报好过"下完几十 GiB 后 EOCD 报错"）。
    /// 无序号形态/单文件/已是分卷标记的原样返回。
    /// </summary>
    private GameManifest EnsureSplitVolumeManifest(GameManifest manifest)
    {
        if (!manifest.EntriesAreArchives || manifest.ArchivesAreSplitVolumes || manifest.Files.Count < 2)
        {
            return manifest;
        }

        var (kind, order) = SplitVolumeNaming.Classify([.. manifest.Files.Select(f => f.Path)]);
        if (kind == SplitVolumeSet.NotVolumes)
        {
            return manifest;
        }

        if (kind == SplitVolumeSet.Broken)
        {
            throw new UpdateException(
                "Staged predownload looks like split volumes but is not a contiguous single set: " +
                string.Join(", ", manifest.Files.Select(f => f.Path)) + "; re-run predownload.");
        }

        logger?.LogInformation(
            "Staged manifest without the split-volume flag restored as split volumes: {Count} volumes.", manifest.Files.Count);
        return new GameManifest
        {
            Version = manifest.Version,
            Files = [.. order.Select(index => manifest.Files[index])],
            Groups = manifest.Groups,
            DeleteFiles = manifest.DeleteFiles,
            EntriesAreArchives = manifest.EntriesAreArchives,
            ArchivesAreSplitVolumes = true,
        };
    }

    /// <summary>
    /// 解压一批已就绪的暂存包并返回产物（归一相对路径）：常规清单逐包解压；分卷清单
    /// （<see cref="GameManifest.ArchivesAreSplitVolumes"/>）把各卷按清单顺序整体拼接后解压一次——
    /// 分卷单取不是合法 zip（中央目录与 EOCD 在末卷）。产物集合供废弃文件清理扣除
    /// （清单自相矛盾点名产物时不得删除，RF-C 同纪律）。
    /// </summary>
    private static List<string> ExtractStagedArchives(
        IReadOnlyList<(ManifestFile File, string ArchivePath)> staged,
        bool archivesAreSplitVolumes,
        string installDir)
    {
        if (archivesAreSplitVolumes)
        {
            return ExtractSplitArchive([.. staged.Select(s => s.ArchivePath)], installDir);
        }

        var produced = new List<string>();
        foreach (var (file, archivePath) in staged)
        {
            produced.AddRange(ExtractArchive(archivePath, installDir, file.Path));
        }

        return produced;
    }

    /// <summary>
    /// 删除官方点名的废弃文件（渠道 GetObsoleteFilesAsync，终末地
    /// delete_files.txt 实测 324 条 / 47.72 GiB）并回收腾空的目录：不清理会让旧版残留随版本累积
    /// （包式渠道无逐文件清单，解压只覆盖不删除）。
    /// 防线：路径归一后必须落在安装目录内且不穿过链接（清单不可信）；本次解压产物一律不删
    /// （清单自相矛盾时以产物为准）；状态目录（<c>.yagl</c>）不删。单条失败只记日志继续
    /// （Windows 占用等），清理失败不得让已完成的解压整体回滚。
    /// </summary>
    internal static int DeleteObsoleteFiles(
        string installDir,
        IReadOnlyList<string>? obsoleteFiles,
        IReadOnlyCollection<string> produced,
        IProgress<UpdateProgress>? progress,
        ILogger? logger)
    {
        if (obsoleteFiles is not { Count: > 0 })
        {
            return 0;
        }

        if (produced.Count == 0)
        {
            // 本轮没有任何产物落地（清单为空/下载空转）：绝不按删除清单清理——否则会把安装目录
            // 清成半空、随后版本号还被写新（review P2-2，2026-10-17）
            logger?.LogWarning("Obsolete file cleanup skipped: this run produced no files.");
            return 0;
        }

        progress?.Report(new UpdateProgress(UpdatePhase.CleaningUp, 0, 0, 0, obsoleteFiles.Count, null));
        var producedSet = produced.ToHashSet(StagedPathComparer);
        var deleted = 0;
        var skipped = 0;
        foreach (var obsolete in obsoleteFiles)
        {
            var normalized = NormalizeRelativePath(obsolete);
            if (normalized is null
                || producedSet.Contains(normalized)
                || normalized.StartsWith(LocalStateService.StateDirName + "/", StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            var target = Path.Combine([installDir, .. normalized.Split('/')]);
            try
            {
                // 既有目录符号链接会让删除穿透到沙箱之外（F19 同族）：命中即跳过该条
                FileUtilities.EnsureNoReparseWithin(installDir, target);
            }
            catch (IOException ex)
            {
                // 沙箱复核兜住链接与拼合差异（词法 ".." 拒绝是前一层防线）
                logger?.LogWarning("Skip obsolete file outside the sandbox or behind a link: {Path} ({Reason})", normalized, ex.Message);
                skipped++;
                continue;
            }

            try
            {
                if (!File.Exists(target))
                {
                    skipped++;
                    continue;
                }

                File.Delete(target);
                deleted++;
                PruneEmptyParents(installDir, Path.GetDirectoryName(target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "Failed to delete obsolete file: {Path}", normalized);
                skipped++;
            }
        }

        logger?.LogInformation(
            "Obsolete file cleanup finished: deleted {Deleted}, skipped {Skipped} (of {Total} listed).",
            deleted, skipped, obsoleteFiles.Count);
        return deleted;
    }

    /// <summary>
    /// 删除废弃文件后回收腾空的目录：自落点父目录向上逐级删空目录，遇到非空/链接/安装根即停
    /// （终末地旧分块删除后遗留成百上千个空分片目录）。
    /// </summary>
    private static void PruneEmptyParents(string installDir, string? directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        var current = directory;
        while (current is not null
               && current.Length > root.Length
               && current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            try
            {
                if (!Directory.Exists(current)
                    || FileUtilities.IsReparsePoint(current)
                    || Directory.EnumerateFileSystemEntries(current).Any())
                {
                    return;
                }

                Directory.Delete(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // 目录被占用/删不动：留给下次清理，不是错误
            }

            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>清单路径归一：''→'/'、去首部 '/'、去空段；含 ".." 或（归一后仍）为根路径的拒绝（返回 null）。</summary>
    private static string? NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(normalized))
        {
            return null;
        }

        var segments = normalized.Split('/').Where(segment => segment.Length > 0).ToArray();
        return segments.Length == 0 || segments.Contains("..", StringComparer.Ordinal)
            ? null
            : string.Join('/', segments);
    }

    /// <summary>压缩包在包目录内的落盘路径（清单里的反斜杠路径取末段文件名）。</summary>
    private static string StagedArchivePath(string packagesDir, ManifestFile file) =>
        Path.Combine(packagesDir, Path.GetFileName(file.Path.Replace('\\', '/')));

    /// <summary>暂存包完整性 TOCTOU 确定性测试缝（生产恒 null）：Exists 通过后、实际探测前
    /// 以暂存包路径触发——测试注入"检查与操作之间文件被外部移除"的窗口（F18 缝同款）。
    /// 仅测试调用。</summary>
    internal static Action<string>? StagedArchiveRemovedBetweenCheckAndProbeForTests;

    /// <summary>暂存包完整性：文件存在、大小与 MD5 均与清单一致。size ≤ 0 或 MD5 为空表示
    /// 渠道未提供该字段的校验信息，只按存在性视为完好（与 <see cref="HttpFileDownloader.Verify"/>
    /// 同一语义）——否则字段缺失的暂存包恒判损坏，每次应用都整包重下（2026-09-20 复审补齐）。
    /// Exists→Length/Md5 之间暂存包被外部移除（并发删除/手删暂存）按"不完整"报告走重下，
    /// 不让裸 FNFE 穿出折算成 Unknown（F18 同型 TOCTOU，次级 suspect 第 13 轮）。</summary>
    internal static bool IsArchiveIntact(string archivePath, ManifestFile file)
    {
        if (!File.Exists(archivePath))
        {
            return false;
        }

        try
        {
            StagedArchiveRemovedBetweenCheckAndProbeForTests?.Invoke(archivePath);
            return (file.Size <= 0 || new FileInfo(archivePath).Length == file.Size)
                && (string.IsNullOrEmpty(file.Md5)
                    || Hashing.Md5Hex(archivePath).Equals(file.Md5, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private async Task<List<(ManifestFile File, string ArchivePath)>> DownloadPackagesAsync(
        string packagesDir,
        GameManifest packageManifest,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(packagesDir);
        var totalBytes = packageManifest.Files.Sum(f => f.Size);
        var staged = new List<(ManifestFile, string)>();
        var seenTargets = new HashSet<string>(StagedPathComparer);

        // 字节级实时进度（2026-10-02 用户点名"进度条随时更新"）：整包下载期间进度条按字节推进，
        // 不再只在包完成时跳变（此前给下载器传 null 进度）。回调读循环内更新的当前条目局部变量。
        ManifestFile? currentFile = null;
        var currentIndex = 0;
        var aggregator = new ByteProgressAggregator(
            TimeSpan.FromMilliseconds(100),
            bytes => progress?.Report(new UpdateProgress(
                UpdatePhase.Downloading, totalBytes, bytes, currentIndex, packageManifest.Files.Count, currentFile?.Path)));

        foreach (var (file, index) in packageManifest.Files.Select((f, i) => (f, i)))
        {
            currentFile = file;
            currentIndex = index;
            ManifestChecks.EnsureDownloadUrl(file.Url, "Package entry", file.Path);

            var archivePath = StagedArchivePath(packagesDir, file);
            // 暂存落盘按清单路径末段命名：两个条目同名（不同 URL 目录）会互踩——后包覆盖先包，
            // 解压循环把同一文件解两次、先包内容从未落地且全程无错误（2026-09-20 复审修复）
            if (!seenTargets.Add(Path.GetFullPath(archivePath)))
            {
                throw new UpdateException(
                    $"Two package entries share the staged file name \"{file.Path}\"; refusing to silently overwrite one with the other.");
            }

            if (!IsArchiveIntact(archivePath, file))
            {
                var perFile = aggregator.CreateFileProgress();
                await downloader.DownloadFileAsync(
                    new DownloadRequest(file.Url, archivePath, file.Size, file.Md5), perFile, cancellationToken).ConfigureAwait(false);
                aggregator.Add(file.Size - perFile.ConsumedBytes); // 零回调/差额兜底，完成时恰好 +size
                aggregator.ForceReport();
            }
            else
            {
                aggregator.Add(file.Size); // 已就绪的整包计入全局进度
                aggregator.ForceReport(); // 条目边界强制投递：末帧精确到 totalBytes（节流会吞 Add 的投递）
            }

            staged.Add((file, archivePath));
        }

        return staged;
    }

    /// <summary>默认解压体量上限（字节）：小压缩包吃 1GiB 绝对下限（合法补丁包永不被误杀），
    /// 大压缩包吃 100 倍比例（合法包内容本已压缩、解压比≈1-3，炸弹比例成千）。</summary>
    internal static long DeriveMaxExtractBytes(long archiveBytes) =>
        Math.Max(archiveBytes * 100, 1L << 30);

    /// <summary>防御式 zip 解压（体量上限/路径逃逸/异常折算 UpdateException），包式渠道安装与
    /// 外部工具供给（如 hpatchz 自动下载，Channels.Kuro）共用。</summary>
    public static List<string> ExtractArchive(string archivePath, string installDir, string displayName,
        long? maxExtractBytes = null)
    {
        try
        {
            // 体量上限取归档尺寸置于折算 try 内（D1）：归档被删/路径拼错的 FNFE（⊂ IOException）
            // 否则从 try 之前裸穿，与"除取消外全部折算"的分类纪律不一致
            var cap = maxExtractBytes ?? DeriveMaxExtractBytes(new FileInfo(archivePath).Length);
            using var archive = ZipFile.OpenRead(archivePath);
            return ExtractEntries(archive, installDir, displayName, cap);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"Failed to extract {displayName}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 分卷归档解压（<see cref="GameManifest.ArchivesAreSplitVolumes"/>，终末地 packs 形态）：
    /// 把按分卷顺序排列的物理文件经 <see cref="SplitVolumeStream"/> 呈现为一个可寻址流交给
    /// ZipArchive，不落盘拼接副本（数十 GB 分卷归档不产生等量临时文件与复制时间）。
    /// 体量上限按分卷合计推导，防线与 <see cref="ExtractArchive"/> 共用同一解压循环。
    /// </summary>
    internal static List<string> ExtractSplitArchive(IReadOnlyList<string> volumePaths, string installDir)
    {
        try
        {
            if (volumePaths.Count == 0)
            {
                throw new IOException("Split archive has no volumes.");
            }

            // 体量上限取分卷合计尺寸，同样置于折算 try 内（D1）：分卷缺失的 FNFE 一并折算
            long totalBytes = 0;
            foreach (var path in volumePaths)
            {
                totalBytes += new FileInfo(path).Length;
            }

            using var archive = new ZipArchive(new SplitVolumeStream(volumePaths), ZipArchiveMode.Read);
            return ExtractEntries(archive, installDir, SplitArchiveDisplayName(volumePaths), DeriveMaxExtractBytes(totalBytes));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"Failed to extract {SplitArchiveDisplayName(volumePaths)}: {ex.Message}", ex);
        }
    }

    /// <summary>分卷归档的展示名：末卷名去掉 <c>.NNN</c> 序号（…official.zip.059 → …official.zip）。</summary>
    private static string SplitArchiveDisplayName(IReadOnlyList<string> volumePaths) =>
        volumePaths.Count == 0 ? "split archive" : Path.GetFileNameWithoutExtension(volumePaths[^1]);

    /// <summary>
    /// 归档条目循环：体量记账（zip bomb）、沙箱与链接防线、逐条目落盘与声明尺寸对账。
    /// 单文件归档与分卷拼接流共用（调用方负责打开归档流并把异常折算 UpdateException）。
    /// </summary>
    private static List<string> ExtractEntries(ZipArchive archive, string installDir, string displayName, long cap)
    {
        long declaredTotal = 0;
        var produced = new List<string>(archive.Entries.Count);
        // 不用 ZipFile.ExtractToDirectory：它在 Unix 上把含 '\' 的条目名当字面文件名
        // （dotnet/runtime#98247），Windows 打包器产出的包会在 Linux 解成安装根目录下的
        // 一堆平铺垃圾文件。手动遍历统一归一 '/'，顺带做沙箱校验与只读属性处理。
        foreach (var entry in archive.Entries)
        {
            // 目录条目（null）由 ResolveEntryTarget 校验后就地建目录；
            // 调用方不得再用未清洗的原始条目名拼接，否则 rooted 名经 Path.Combine 逃出沙箱
            if (ResolveEntryTarget(installDir, entry.FullName) is not { } resolved)
            {
                continue;
            }

            var target = resolved.Target;
            produced.Add(resolved.Relative);

            // 体量记账：zip bomb 的体量在中央目录的声明尺寸可见，累计超阈值即拒，
            // 超限条目不落盘（加固项，artifacts/bugs.md——威胁模型下路径有防线、体量原本没有）
            declaredTotal += entry.Length;
            if (declaredTotal > cap)
            {
                throw new IOException(
                    $"Archive declares {declaredTotal} bytes total, exceeding extraction cap {cap}: {displayName}");
            }

            // 既有目录符号链接会让词法沙箱失效：CreateDirectory 对链接目录是 no-op，
            // ExtractToFile 经链接写出沙箱（F19——词法防穿越管不住 reparse 点）
            FileUtilities.EnsureNoReparseWithin(installDir, target);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // 目标自身是文件符号链接同样写穿：ExtractToFile 的 open(O_CREAT) 跟随链接
            // 改写/创建沙箱外的真实文件（复审 R2 /tmp 探针实锤，含悬空链接变体）——
            // 检查必须无条件执行：Windows 的 File.Exists 对悬空链接为 false，放在其门内
            // 会让悬空形态绕过检查
            if (FileUtilities.IsReparsePoint(target))
            {
                throw new IOException($"Archive target is an existing symlink: {target}");
            }

            if (File.Exists(target))
            {
                // 硬链接没有 ReparsePoint 标记（F43）：File.GetAttributes 返回共享 inode 的
                // 属性，上述符号链接防线对它全部失效——ExtractToFile(overwrite:true) 沿既有
                // inode 写会截断改写同卷沙箱外的真实文件。链接数 > 1 = 与沙箱外共享 inode，拒绝
                if (FileUtilities.HardLinkCount(target) > 1)
                {
                    throw new IOException($"Archive target is an existing hard link: {target}");
                }

                // 只读属性会让 overwrite 失败（Windows），就地解除
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
            }

            entry.ExtractToFile(target, overwrite: true);

            // 中央目录可能谎报声明尺寸（定制炸弹声明 10 实际吐 GB 级）：写完实测对账，
            // 撒谎条目在写入后立即暴露（诚实打包的条目两者恒等）
            if (new FileInfo(target).Length != entry.Length)
            {
                throw new IOException(
                    $"Archive entry {entry.FullName} declares {entry.Length} bytes but wrote {new FileInfo(target).Length}");
            }
        }

        return produced;
    }

    /// <summary>
    /// 解压条目落点：归一 '\'→'/'、去首部 '/'，拒绝 ".." 段与盘符根（清单不可信，防穿越——
    /// 目录条目与文件条目一视同仁）；目录条目（含空名）在校验通过后就地建目录并返回 null，
    /// 普通条目返回（安装目录内绝对落点, 归一相对路径）——相对路径供废弃文件清理扣除本次产物。
    /// 所有路径拼接只使用校验后的分段。
    /// </summary>
    private static (string Target, string Relative)? ResolveEntryTarget(string installDir, string entryName)
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
            FileUtilities.EnsureNoReparseWithin(installDir, dirTarget); // 目录条目同样不得穿过既有链接（F19）
            Directory.CreateDirectory(dirTarget);
            return null;
        }

        return (Path.Combine([installDir, .. segments]), string.Join('/', segments));
    }

}
