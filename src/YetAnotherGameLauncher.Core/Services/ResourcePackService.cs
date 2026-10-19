using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>单个档位在本地已落盘的量（预览/清理文案用）。</summary>
/// <param name="Tier">档位代号。</param>
/// <param name="PackDirectory">该档位在安装目录下的资源目录（相对路径）。</param>
/// <param name="PresentFiles">存在且尺寸一致的完整文件数（快速校验口径，不核 MD5）。</param>
/// <param name="TotalFiles">清单文件总数。</param>
/// <param name="PresentBytes">完整文件占用的字节数。</param>
/// <param name="TotalBytes">清单声明的总字节数。</param>
/// <param name="TempFiles">下载半成品（<c>.temp</c>）数量——同步收尾对保留档位目录下的一切都跳过，
/// 显式清理是它们唯一的清道夫（2026-10-19 review）。</param>
/// <param name="TempBytes">下载半成品占用的字节数。</param>
public sealed record ResourcePackUsage(
    string Tier,
    string PackDirectory,
    int PresentFiles,
    int TotalFiles,
    long PresentBytes,
    long TotalBytes,
    int TempFiles = 0,
    long TempBytes = 0)
{
    /// <summary>该档位是否已完整落盘（全部清单文件存在且尺寸一致）：清理确认文案据此判断
    /// "删掉其它档后游戏还有没有完整的档可用"。</summary>
    public bool Complete => TotalFiles > 0 && PresentFiles == TotalFiles;

    /// <summary>本次清理可释放的字节数（完整文件 + 半成品残件）。</summary>
    public long ReclaimableBytes => PresentBytes + TempBytes;
}

/// <summary>
/// 显式清理的预览（确认文案用；不改任何文件）。
/// <paramref name="KeptTierKnown"/>：选中档位的清单是否取到（false = 落盘状态未知，文案不得断言"未下载"）。
/// <paramref name="KeptTierPresent"/>：选中档位是否有已落盘文件（可能不完整）。
/// <paramref name="KeptTierComplete"/>：选中档位是否完整落盘——不完整即"清理后游戏可能缺档"，
/// 确认文案据此加警示（半途下载同样会缺档，不能只看"有没有文件"）。
/// <paramref name="UnavailableTiers"/>：清单取不到的档位（网络/服务端问题），本次不会清理，文案须如实说明。
/// </summary>
public sealed record ResourcePackCleanupPreview(
    string KeptTier,
    bool KeptTierKnown,
    bool KeptTierPresent,
    bool KeptTierComplete,
    IReadOnlyList<ResourcePackUsage> Targets,
    IReadOnlyList<string> UnavailableTiers)
{
    /// <summary>本次将释放的字节数（完整文件 + 半成品残件）。</summary>
    public long ReclaimableBytes => Targets.Sum(t => t.ReclaimableBytes);
}

/// <summary>显式清理其它档位的结果。</summary>
public sealed record ResourcePackCleanupResult(
    string KeptTier,
    int RemovedFiles,
    long FreedBytes,
    int FailedFiles,
    IReadOnlyList<string> UnavailableTiers);

/// <summary>
/// 档位资源包的本地面（2026-10-19 用户需求）：测体积与显式清理其它档位。
/// 隐式清理（同步收尾的 <see cref="GameInstallService"/> CleanupStaleFiles）永不碰档位目录
/// （渠道用 <see cref="GameManifest.PreservedPaths"/> 声明保留）——删除只经本服务，
/// 且只删各档位清单点名、解析后确实位于该档位目录之下的文件（含下载半成品 <c>.temp</c>）：
/// 游离文件（不在任何清单里）留给同步清理，互不越界。
/// </summary>
public sealed class ResourcePackService(IGameChannelApi channel, ILogger? logger = null)
{
    /// <summary>档位包在本地的落盘度量：逐文件核对存在性与尺寸（快速校验口径，不算 MD5），
    /// 并统计同一命名的下载半成品（<c>.temp</c>）。</summary>
    public static ResourcePackUsage Measure(string installDir, ResourcePackManifest manifest)
    {
        var presentFiles = 0;
        var presentBytes = 0L;
        var tempFiles = 0;
        var tempBytes = 0L;
        foreach (var file in manifest.Files)
        {
            if (TryMeasureEntry(installDir, manifest.PackDirectory, file, out var present, out var temp))
            {
                if (present > 0)
                {
                    presentFiles++;
                    presentBytes += present;
                }

                if (temp > 0)
                {
                    tempFiles++;
                    tempBytes += temp;
                }
            }
        }

        return new ResourcePackUsage(
            manifest.Tier, manifest.PackDirectory, presentFiles, manifest.Files.Count,
            presentBytes, manifest.Files.Sum(f => f.Size), tempFiles, tempBytes);
    }

    /// <summary>清理前的预览：选中档位是否完整在盘 + 其它档位各已落盘多少；清单取不到的档位如实列举
    /// （逐档容错：一个档位取不到清单不该让整页报错——与清理链"尽力而为"同契约，2026-10-19 review）。</summary>
    public async Task<ResourcePackCleanupPreview> PreviewCleanupAsync(
        string installDir, GameServer server, string? selectedTier, CancellationToken cancellationToken = default)
    {
        var kept = ResourceTiers.Effective(selectedTier);
        ResourcePackManifest? keptPack;
        try
        {
            keptPack = await TryGetPackAsync(server, kept, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "档位 {Tier} 清单取不到，预览按\"落盘状态未知\"处理", kept);
            keptPack = null;
        }

        var keptUsage = keptPack is not null ? Measure(installDir, keptPack) : null;

        var targets = new List<ResourcePackUsage>();
        var unavailable = new List<string>();
        if (keptPack is null)
        {
            // 选中档位清单也取不到：如实列入"清单取不到"（文案不得断言"未下载"，也不得沉默——
            // 用户看到的是"清理后选中档位是否完整"这个安全问题的答案未知）
            unavailable.Add(kept);
        }

        foreach (var tier in ResourceTiers.OtherTiers(kept))
        {
            ResourcePackManifest? pack;
            try
            {
                pack = await TryGetPackAsync(server, tier, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "档位 {Tier} 清单取不到，预览按不可用处理", tier);
                unavailable.Add(tier);
                continue;
            }

            if (pack is null)
            {
                unavailable.Add(tier);
                continue;
            }

            targets.Add(Measure(installDir, pack));
        }

        return new ResourcePackCleanupPreview(
            kept,
            KeptTierKnown: keptPack is not null,
            KeptTierPresent: keptUsage is { PresentFiles: > 0 },
            KeptTierComplete: keptUsage?.Complete == true,
            targets,
            unavailable);
    }

    /// <summary>
    /// 清理其它档位（生效档之外）在本地的落盘文件：只删各档位清单点名、且<b>解析后</b>确实位于该档位
    /// 目录之下的文件（词法前缀命中但带 <c>..</c> 段的越界条目一并拒删）与同名下载半成品；
    /// 逐文件容错（只读/权限失败跳过并计数）；删空目录。返回释放量。
    /// </summary>
    public async Task<ResourcePackCleanupResult> CleanupOtherTiersAsync(
        string installDir, GameServer server, string? selectedTier, CancellationToken cancellationToken = default)
    {
        var kept = ResourceTiers.Effective(selectedTier);
        var removed = 0;
        var failed = 0;
        var freed = 0L;
        var unavailable = new List<string>();

        foreach (var tier in ResourceTiers.OtherTiers(kept))
        {
            ResourcePackManifest? pack;
            try
            {
                pack = await TryGetPackAsync(server, tier, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单个档位清单取不到不影响其它档位：跳过并如实上报（清理是尽力而为）
                logger?.LogWarning(ex, "档位 {Tier} 清单取不到，跳过清理", tier);
                unavailable.Add(tier);
                continue;
            }

            if (pack is null)
            {
                unavailable.Add(tier);
                continue;
            }

            var prefix = pack.PackDirectory.Replace('\\', '/').TrimEnd('/') + "/";
            foreach (var file in pack.Files)
            {
                var relative = file.Path.Replace('\\', '/');
                // 只删档位目录内的清单文件：越界条目（清单被篡改/渠道形态漂移）一律跳过——
                // 与同步清理"绝不越出安装目录"同纪律
                if (!relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryDeleteFile(installDir, pack.PackDirectory, relative, out var size, out var error))
                {
                    removed++;
                    freed += size;
                }
                else if (error is not null)
                {
                    failed++;
                }

                // 同名下载半成品（下载器命名约定 <目标>.temp）：保留目录下的残件同步收尾不碰，
                // 显式清理是唯一回收点（2026-10-19 review：此前预览说"没得清"、清理也不删，
                // 中止的其它档位下载残件会永远留在盘上）。完整文件缺失时同样要查——只留 .temp
                // 的中止下载正是最常见的残件形态
                if (TryDeleteFile(installDir, pack.PackDirectory, relative + ".temp", out var tempSize, out var tempError))
                {
                    removed++;
                    freed += tempSize;
                }
                else if (tempError is not null)
                {
                    failed++;
                }
            }

            RemoveEmptyDirectories(Path.Combine(installDir, pack.PackDirectory));
        }

        if (removed > 0 || failed > 0)
        {
            logger?.LogInformation(
                "Resource pack cleanup for kept tier {Kept}: removed {Removed} file(s), freed {Bytes} bytes, {Failed} failure(s).",
                kept, removed, freed, failed);
        }

        return new ResourcePackCleanupResult(kept, removed, freed, failed, unavailable);
    }

    /// <summary>取档位包清单；渠道不支持该档位（null）时返回 null，取不到（网络/服务端）时抛。</summary>
    private Task<ResourcePackManifest?> TryGetPackAsync(GameServer server, string tier, CancellationToken cancellationToken) =>
        channel.GetResourcePackManifestAsync(server, tier, cancellationToken);

    /// <summary>单条目度量：完整文件与同名半成品各返回其字节数（缺失/越界为 0，不抛：度量只服务文案）。</summary>
    private static bool TryMeasureEntry(
        string installDir, string packDirectory, ManifestFile file, out long present, out long temp)
    {
        present = 0;
        temp = 0;
        try
        {
            if (!IsInsidePackDirectory(installDir, packDirectory, file.Path))
            {
                return false;
            }

            var full = ManifestVerifier.ResolveSafe(installDir, file.Path);
            var info = new FileInfo(full);
            if (info.Exists && info.Length == file.Size)
            {
                present = info.Length;
            }

            var tempInfo = new FileInfo(full + ".temp");
            if (tempInfo.Exists)
            {
                temp = tempInfo.Length;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UpdateException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 清单条目解析后是否确实位于该档位目录之内：词法前缀（<see cref="ResourcePackCleanupResult"/> 侧）
    /// 挡不住 <c>..</c> 段——<c>Client/Content/HD/../SD/x.pak</c> 前缀命中 HD 却解析到保留档 SD，
    /// 故以<b>解析结果</b>为准（2026-10-19 review 的纵深修复）。解析越出安装目录按"不在档位内"处理。
    /// </summary>
    private static bool IsInsidePackDirectory(string installDir, string packDirectory, string relative)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(
                Path.GetFullPath(installDir), packDirectory.Replace('\\', '/').Trim('/')));
            var candidate = ManifestVerifier.ResolveSafe(installDir, relative);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
        }
        catch (Exception ex) when (ex is UpdateException or ArgumentException or NotSupportedException
            or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>删单个文件：已删除返回 true（size = 释放字节数）；本就不存在或越界
    /// （解析后不在该档位目录内，静默跳过不算失败）返回 false 且 error=null；删除失败 error 非空。</summary>
    private bool TryDeleteFile(
        string installDir, string packDirectory, string relative, out long size, out string? error)
    {
        size = 0;
        error = null;
        if (!IsInsidePackDirectory(installDir, packDirectory, relative))
        {
            logger?.LogWarning("档位清理拒绝越界条目（未删除）：{Path}", relative);
            return false;
        }

        try
        {
            var full = ManifestVerifier.ResolveSafe(installDir, relative);
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                return false;
            }

            size = info.Length;
            File.Delete(full);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UpdateException
            or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            logger?.LogWarning(ex, "档位清理失败（跳过）：{Path}", relative);
            return false;
        }
    }

    /// <summary>自底向上删除清理后变空的目录（失败只记日志；不递归删除非空目录）。</summary>
    private void RemoveEmptyDirectories(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var dir in Directory.EnumerateDirectories(directory, "*", options).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogDebug(ex, "空目录清理跳过：{Path}", dir);
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogDebug(ex, "空目录清理跳过：{Path}", directory);
        }
    }
}
