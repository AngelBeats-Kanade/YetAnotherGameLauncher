using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Compressors.Xz;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 原生 umu 兼容组件准备器：解析/下载 DW-Proton / GE-Proton / UMU-Proton 到 compatibilitytools.d，
/// 下载 Steam Linux Runtime 到 ~/.local/share/umu/&lt;variant&gt;。纯 C#，不依赖 Python umu-run。
/// DW-Proton 托管在 dawn.wine（Forgejo，API 兼容 GitHub releases 字段但 latest 为数组根）；GE/UMU 在 GitHub。
/// 资产按主机架构过滤（hostArchitecture 可注入供测试），解压后再以 wineserver 的 ELF 头兜底校验。
/// </summary>
public sealed class UmuComponentProvisioner(
    HttpClient httpClient,
    IDownloader downloader,
    ILogger? logger = null,
    string? dataHome = null,
    string? cacheHome = null,
    Architecture? hostArchitecture = null,
    ILocalizationService? loc = null) : IUmuComponentProvisioner
{
    /// <summary>本地化文案（F12，2026-09-24 迁移）：进度/错误消息经 strings_*.json 双语成对；
    /// 不注入即默认 zh-CN（与旧字面量等值），既有测试断言不受影响。</summary>
    private readonly ILocalizationService loc = loc ?? new LocalizationService();
    /// <summary>主机（或测试注入）架构；资产过滤与 ELF 兜底校验的判定基准。</summary>
    private Architecture HostArchitecture => hostArchitecture ?? RuntimeInformation.ProcessArchitecture;
    /// <summary>GE-Proton 最新 release 的 GitHub API。</summary>
    public const string GeProtonReleaseApi =
        "https://api.github.com/repos/GloriousEggroll/proton-ge-custom/releases/latest";

    /// <summary>UMU-Proton 最新 release 的 GitHub API。</summary>
    public const string UmuProtonReleaseApi =
        "https://api.github.com/repos/Open-Wine-Components/UMU-Proton/releases/latest";

    /// <summary>DW-Proton（Dawn Winery）最新 release 的 Forgejo API（数组根，取首个非 draft/prerelease）。</summary>
    public const string DwProtonReleaseApi =
        "https://dawn.wine/api/v1/repos/dawn-winery/dwproton/releases?limit=1";

    /// <summary>Steam Runtime 镜像主机。</summary>
    public const string RuntimeHost = "https://repo.steampowered.com";

    /// <summary>Proton 发行版定义：代号 → 下载源（latest / 按 tag）与资产、本地目录前缀。</summary>
    private sealed record ProtonFlavor(
        string Codename,
        string LatestApi,
        string TagApiFormat,
        string AssetPrefix,
        string LocalPrefix);

    /// <summary>支持的 Proton 发行版表（按代号前缀匹配：UMU* / GE* / DW*，忽略大小写）。</summary>
    private static readonly ProtonFlavor[] ProtonFlavorTable =
    [
        new("UMU-Proton", UmuProtonReleaseApi,
            "https://api.github.com/repos/Open-Wine-Components/UMU-Proton/releases/tags/{0}",
            "UMU-Proton", "UMU-Proton"),
        new("GE-Proton", GeProtonReleaseApi,
            "https://api.github.com/repos/GloriousEggroll/proton-ge-custom/releases/tags/{0}",
            "GE-Proton", "GE-Proton"),
        new("DW-Proton", DwProtonReleaseApi,
            "https://dawn.wine/api/v1/repos/dawn-winery/dwproton/releases/tags/{0}",
            "dwproton", "dwproton"),
    ];

    /// <summary>按代号/版本名前缀匹配发行版（UMU* → UMU，GE* → GE，DW* / dwproton* → DW）；未知返回 null。</summary>
    private static ProtonFlavor? MatchFlavor(string protonRequest)
    {
        foreach (var flavor in ProtonFlavorTable)
        {
            // 双形态前缀：官方代号（Codename，如 "DW-Proton"）与本地目录名（LocalPrefix，
            // 如 "dwproton-11.0-12"——已装目录名复制来的请求是自然输入，3 字符截断匹配不了它）
            if (protonRequest.StartsWith(flavor.Codename[..3], StringComparison.OrdinalIgnoreCase)
                || protonRequest.StartsWith(flavor.LocalPrefix + "-", StringComparison.OrdinalIgnoreCase))
            {
                return flavor;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public bool IsProtonReady(string protonPath)
    {
        if (string.IsNullOrWhiteSpace(protonPath) || !Directory.Exists(protonPath))
        {
            return false;
        }

        return File.Exists(Path.Combine(protonPath, "toolmanifest.vdf"))
               && File.Exists(Path.Combine(protonPath, "proton"));
    }

    /// <inheritdoc />
    public bool IsRuntimeReady(string runtimeVariant)
    {
        if (string.IsNullOrWhiteSpace(runtimeVariant))
        {
            return true;
        }

        var dir = UmuPaths.RuntimeDirectory(runtimeVariant, dataHome);
        return Directory.Exists(dir)
               && File.Exists(Path.Combine(dir, UmuPaths.InstallMarkerName))
               && (File.Exists(Path.Combine(dir, "_v2-entry-point")) || File.Exists(Path.Combine(dir, "umu")));
    }

    /// <inheritdoc />
    public async Task<string> EnsureProtonAsync(
        string protonRequest,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(protonRequest))
        {
            protonRequest = CompatTools.DefaultProtonFlavor;
        }

        // 本地已就绪即用（含代号前缀最新）——启动/组件准备不联网、不静默拉 latest；更新走 UpdateProtonAsync
        var ready = FindInstalledProton(protonRequest);
        if (ready is not null)
        {
            return ready;
        }

        // 缺失才下载：代号走 latest，具体版本名只下该 tag
        progress?.Report(loc.Format("umu_progress_prepareProton", protonRequest));
        if (IsCodename(protonRequest))
        {
            return await DownloadLatestProtonAsync(protonRequest, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        return await DownloadProtonByTagAsync(protonRequest, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public (string Variant, string Name)? ResolveRequiredRuntime(string protonRequest)
    {
        var path = FindInstalledProton(protonRequest);
        if (path is null)
        {
            return null;
        }

        try
        {
            var runtime = ToolManifest.Load(path).RequiredRuntime;
            if (runtime.Name == "host" || string.IsNullOrEmpty(runtime.Variant))
            {
                // 已装 Proton 声明 host（免容器直跑）是已知事实，返回它而不是 null——
                // null 保留给"Proton 缺失/清单不可读"的未知情形（调用方回退 steamrt4 近似）；
                // 声明 host 却按 steamrt4 准备会让设置页与启动路径互相矛盾（2026-09-20 复审修复）。
                // 空 variant 在 IsRuntimeReady/EnsureRuntimeAsync 里即"无需准备"
                return (runtime.Variant, runtime.Name);
            }

            return (runtime.Variant, runtime.Name);
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
        {
            // 清单不可读：调用方回退默认 Runtime
            return null;
        }
    }

    /// <inheritdoc />
    public string? FindInstalledProton(string protonRequest)
    {
        if (string.IsNullOrWhiteSpace(protonRequest))
        {
            protonRequest = CompatTools.DefaultProtonFlavor;
        }

        if (Path.IsPathRooted(protonRequest) && IsProtonReady(protonRequest))
        {
            return Path.GetFullPath(protonRequest);
        }

        // 已装目录同样做架构校验：错架构的历史残留视同缺失（触发自愈重装），不让启动走到 exec 才失败
        var byName = Path.Combine(UmuPaths.SteamCompatRoot(dataHome), protonRequest);
        if (IsProtonReady(byName) && MatchesHostArch(byName))
        {
            return Path.GetFullPath(byName);
        }

        return IsCodename(protonRequest) ? FindLatestLocalProton(protonRequest) : null;
    }

    /// <inheritdoc />
    public async Task EnsureRuntimeAsync(
        string runtimeVariant,
        string runtimeName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runtimeVariant) || IsRuntimeReady(runtimeVariant))
        {
            return;
        }

        var lockPath = UmuPaths.LockFile($"runtime-{runtimeVariant}.lock");
        using var _ = UmuPrefix.AcquireLock(lockPath);

        if (IsRuntimeReady(runtimeVariant))
        {
            return;
        }

        progress?.Report(loc.Format("umu_progress_downloadRuntime", runtimeVariant));
        await DownloadRuntimeAsync(runtimeVariant, runtimeName, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> FetchLatestProtonTagAsync(
        string protonRequest,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(protonRequest))
        {
            protonRequest = CompatTools.DefaultProtonFlavor;
        }

        var (_, json) = await FetchLatestReleaseAsync(protonRequest, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(json);
            var release = SelectLatestRelease(document.RootElement);
            if (release.ValueKind == JsonValueKind.Object
                && release.TryGetProperty("tag_name", out var tag)
                && !string.IsNullOrWhiteSpace(tag.GetString()))
            {
                return tag.GetString()!;
            }

            throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                loc["umu_err_noTagName"]);
        }
        catch (JsonException ex)
        {
            throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                loc.Format("umu_err_releaseParse", ex.Message),
                ex);
        }
    }

    /// <inheritdoc />
    public async Task<string> UpdateProtonAsync(
        string protonRequest,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(protonRequest))
        {
            protonRequest = CompatTools.DefaultProtonFlavor;
        }

        // 非代号（绝对路径 / 具体版本名）没有发行版前缀语义，退化为确保就绪（不做旧版清理）
        if (!IsCodename(protonRequest))
        {
            return await EnsureProtonAsync(protonRequest, progress, cancellationToken).ConfigureAwait(false);
        }

        var flavor = MatchFlavor(protonRequest)
            ?? throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                loc.Format("umu_err_unsupportedCodename", protonRequest));

        progress?.Report(loc.Format("umu_progress_downloadLatest", protonRequest));
        var newPath = await DownloadLatestProtonAsync(protonRequest, progress, cancellationToken)
            .ConfigureAwait(false);
        PruneOtherProtonVersions(newPath, flavor.LocalPrefix);
        return newPath;
    }

    /// <summary>删除 compatibilitytools.d 下同发行版前缀、非当前目录的已就绪 Proton 旧版本（更新后清理）。
    /// 与安装共用 proton.lock（下载锁已释放，此处串行化清理与并发安装）；删除失败仅记日志不中断——
    /// 旧版残留只占磁盘，不影响运行。</summary>
    private void PruneOtherProtonVersions(string keepPath, string localPrefix)
    {
        using (UmuPrefix.AcquireLock(UmuPaths.LockFile("proton.lock")))
        {
            var root = UmuPaths.SteamCompatRoot(dataHome);
            if (!Directory.Exists(root))
            {
                return;
            }

            var keep = Path.GetFullPath(keepPath);
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)
                    || !IsProtonReady(dir)
                    || string.Equals(Path.GetFullPath(dir), keep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(dir, recursive: true);
                    logger?.LogInformation("Pruned old Proton {Dir}", dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger?.LogWarning(ex, "旧版 Proton 目录删除失败：{Dir}", dir);
                }
            }
        }
    }

    /// <summary>ELF e_machine 常量：x86-64 与 aarch64（Proton 的 wineserver 均为 64 位 ELF）。</summary>
    private const ushort ElfMachineX86_64 = 0x3E;

    /// <summary>ELF e_machine 常量：aarch64。</summary>
    private const ushort ElfMachineAarch64 = 0xB7;

    /// <summary>主机（或测试注入）架构对应的 wineserver ELF e_machine；其余架构 null（无从判别，跳过校验）。</summary>
    private ushort? ExpectedElfMachine => HostArchitecture switch
    {
        Architecture.X64 => ElfMachineX86_64,
        Architecture.Arm64 => ElfMachineAarch64,
        _ => null,
    };

    /// <summary>读 Proton 目录内 files/bin/wineserver 的 ELF e_machine；文件缺失/过短/非 ELF/不可读返回 null（跳过校验）。</summary>
    internal static ushort? ReadWineserverElfMachine(string protonDir)
    {
        var path = Path.Combine(protonDir, "files", "bin", "wineserver");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[20];
            var filled = 0;
            while (filled < header.Length)
            {
                var read = stream.Read(header[filled..]);
                if (read == 0)
                {
                    return null;
                }

                filled += read;
            }

            if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F')
            {
                return null;
            }

            return (ushort)(header[18] | (header[19] << 8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>解压出的 Proton 是否与主机架构一致（wineserver 的 ELF e_machine 对照；无法判别时视为一致）。</summary>
    private bool MatchesHostArch(string protonDir)
    {
        var machine = ReadWineserverElfMachine(protonDir);
        return machine is null || ExpectedElfMachine is not { } expected || machine == expected;
    }

    /// <summary>ELF e_machine → 架构名（错误消息用）。</summary>
    private static string ArchDisplayName(ushort machine) => machine switch
    {
        ElfMachineX86_64 => "x86_64",
        ElfMachineAarch64 => "aarch64",
        _ => $"unknown(0x{machine:X})",
    };

    /// <summary>尽力删除目录（不存在或失败均静默；架构拦截回滚用）。</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>按 release tag 下载指定版本 Proton；找不到该 tag 时抛可操作错误（禁止静默换成最新）。</summary>
    private async Task<string> DownloadProtonByTagAsync(
        string tagName,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        // 未知前缀沿用 GE 源（与既有行为一致，404 时给出可操作错误）
        var flavor = MatchFlavor(tagName) ?? ProtonFlavorTable[1];
        var apiUrl = string.Format(CultureInfo.InvariantCulture, flavor.TagApiFormat, tagName);

        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.UserAgent.ParseAdd("YetAnotherGameLauncher");
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new LaunchException(
                    LaunchFailureKind.ProtonDownloadFailed,
                    loc.Format("umu_err_tagReleaseNotFound", tagName));
            }

            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LaunchException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                loc.Format("umu_err_tagVersionFetch", tagName, ex.Message),
                ex);
        }

        (string Name, string Url) asset;
        try
        {
            using var document = JsonDocument.Parse(json);
            asset = SelectTarAsset(document.RootElement, requiredPrefix: null, ArchSuffixFor(HostArchitecture));
            if (string.IsNullOrEmpty(asset.Url))
            {
                throw new LaunchException(
                    LaunchFailureKind.ProtonDownloadFailed,
                    loc.Format("umu_err_tagNoArchAsset", tagName));
            }
        }
        catch (LaunchException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                loc.Format("umu_err_tagReleaseParse", tagName, ex.Message),
                ex);
        }

        progress?.Report(loc.Format("umu_progress_downloadAsset", asset.Name));
        var (targetDir, tarPath) = ResolveProtonInstallPaths(asset.Name);
        return await InstallProtonAssetAsync(asset, targetDir, tarPath, tagName, cancellationToken);
    }

    /// <summary>按代号（DW-Proton / GE-Proton / UMU-Proton）下载对应仓库的最新构建；返回 Proton 绝对目录。</summary>
    private async Task<string> DownloadLatestProtonAsync(
        string protonRequest,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var (flavor, json) = await FetchLatestReleaseAsync(protonRequest, cancellationToken).ConfigureAwait(false);

        (string Name, string Url) asset;
        try
        {
            using var document = JsonDocument.Parse(json);
            asset = SelectTarAsset(
                SelectLatestRelease(document.RootElement), flavor.AssetPrefix, ArchSuffixFor(HostArchitecture));
            if (string.IsNullOrEmpty(asset.Url))
            {
                throw new UpdateException(
                    loc.Format("umu_err_noArchAsset", flavor.AssetPrefix));
            }
        }
        catch (JsonException ex)
        {
            throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                loc.Format("umu_err_releaseParse", ex.Message),
                ex);
        }

        progress?.Report(loc.Format("umu_progress_downloadAsset", asset.Name));
        var (targetDir, tarPath) = ResolveProtonInstallPaths(asset.Name);
        return await InstallProtonAssetAsync(asset, targetDir, tarPath, tagName: null, cancellationToken);
    }

    /// <summary>GET 发行版 latest API，返回发行版定义与 release JSON 文本（Forgejo 数组根由 SelectLatestRelease 归一）。
    /// 代号不受支持、网络失败抛 LaunchException；下载与"仅查版本"共用。</summary>
    private async Task<(ProtonFlavor Flavor, string Json)> FetchLatestReleaseAsync(
        string protonRequest, CancellationToken cancellationToken)
    {
        var flavor = MatchFlavor(protonRequest)
            ?? throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                loc.Format("umu_err_unsupportedCodename", protonRequest));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, flavor.LatestApi);
            request.Headers.UserAgent.ParseAdd("YetAnotherGameLauncher");
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (flavor, json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                loc.Format("umu_err_versionFetchNetwork", ex.Message),
                ex);
        }
    }

    /// <summary>取"最新 release"元素：GitHub latest 为对象根原样返回；Forgejo（dawn.wine）为数组根，取首个非 draft/prerelease。</summary>
    private static JsonElement SelectLatestRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return root;
        }

        foreach (var release in root.EnumerateArray())
        {
            var draft = release.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
            var pre = release.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
            if (!draft && !pre)
            {
                return release;
            }
        }

        return default;
    }

    /// <summary>从 release 资产里选适配主机架构的 .tar.gz/.tar.xz（排除校验与种子文件）；requiredPrefix 非空时限定文件名前缀。
    /// GitHub 资产顺序即上传顺序（GE-Proton 曾把 aarch64 排在 x86_64 之前），不能取第一个匹配：
    /// 优先带本机架构后缀的资产，其次无架构后缀的资产；与主机相反的架构一律排除。</summary>
    internal static (string Name, string Url) SelectTarAsset(
        JsonElement release, string? requiredPrefix, string? hostArchSuffix)
    {
        if (release.ValueKind != JsonValueKind.Object
            || !release.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        var candidates = assets.EnumerateArray()
            // 畸形元素（非对象/键缺失/键非字符串）按"无此资产"跳过——GetProperty/GetString 的原始
            // KeyNotFoundException/InvalidOperationException 不是 JsonException，会穿出调用点
            // 的解析 catch，把 ProtonDownloadFailed 降级成 Unknown（丢失重试/改选本机 Proton）
            .Select(a => (
                Name: AssetStringOrNull(a, "name") ?? "",
                Url: AssetStringOrNull(a, "browser_download_url") ?? ""))
            .Where(a => a.Name.Length > 0 && a.Url.Length > 0)
            .Where(a => (a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                            || a.Name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
                        && (requiredPrefix is null
                            || a.Name.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
                        && !a.Name.Contains("sha512", StringComparison.OrdinalIgnoreCase)
                        && !a.Name.Contains("sha256sum", StringComparison.OrdinalIgnoreCase)
                        && !a.Name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
                        && IsValidAssetName(a.Name))
            // 带架构后缀但与主机不符的资产排除；主机架构未知（null）时无从判别，保留全部
            .Where(a => AssetArchSuffix(AssetStem(a.Name)) is not { } assetSuffix
                        || hostArchSuffix is null
                        || string.Equals(assetSuffix, hostArchSuffix, StringComparison.Ordinal))
            .ToList();

        var hostMatched = candidates.FirstOrDefault(a =>
            hostArchSuffix is not null
            && string.Equals(AssetArchSuffix(AssetStem(a.Name)), hostArchSuffix, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(hostMatched.Url))
        {
            return hostMatched;
        }

        // 无架构后缀的资产（UMU-Proton 单架构发布形态）对任何主机可用
        return candidates.FirstOrDefault(a => AssetArchSuffix(AssetStem(a.Name)) is null);
    }

    /// <summary>资产元素的安全字段读取：元素非对象或键缺失/非字符串时返回 null（调用方按无此资产跳过）。</summary>
    private static string? AssetStringOrNull(JsonElement asset, string propertyName) =>
        asset.ValueKind == JsonValueKind.Object
        && asset.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>资产名去 .tar.gz/.tar.xz 扩展后的主体（架构后缀判断的基准）。</summary>
    private static string AssetStem(string assetName) =>
        assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? assetName[..^".tar.gz".Length]
            : assetName.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)
                ? assetName[..^".tar.xz".Length]
                : assetName;

    /// <summary>主体名携带的架构后缀（"-x86_64" / "-aarch64"）；没有返回 null。</summary>
    private static string? AssetArchSuffix(string stem)
    {
        foreach (var suffix in ArchSuffixes)
        {
            if (stem.EndsWith(suffix, StringComparison.Ordinal) && stem.Length > suffix.Length)
            {
                return suffix;
            }
        }

        return null;
    }

    /// <summary>架构 → 资产名后缀映射；x86_64/arm64 之外返回 null（无从判别，仅接受无后缀资产）。</summary>
    private static string? ArchSuffixFor(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "-x86_64",
        Architecture.Arm64 => "-aarch64",
        _ => null,
    };

    /// <summary>计算 Proton 压缩包的解压目标目录（compatibilitytools.d 下）与缓存归档路径，并建好所需目录。</summary>
    private (string TargetDir, string TarPath) ResolveProtonInstallPaths(string assetName)
    {
        if (!IsValidAssetName(assetName))
        {
            throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                loc.Format("umu_err_untrustedAssetName", assetName));
        }

        var compatRoot = UmuPaths.SteamCompatRoot(dataHome);
        Directory.CreateDirectory(compatRoot);
        var targetDir = Path.Combine(compatRoot, ProtonExtractDirectoryName(assetName));
        var tarPath = Path.Combine(UmuPaths.CacheRoot(cacheHome), assetName);
        Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);
        return (targetDir, tarPath);
    }

    /// <summary>
    /// 发布资产名白名单（ASCII 字母/数字/点/下划线/连字符，禁首点）：release JSON 的 name
    /// 会直接拼进缓存与安装路径，含分隔符或 ".." 段的名字可逃出目标目录——上游 release 被攻破时的纵深防御。
    /// </summary>
    private static bool IsValidAssetName(string name) =>
        name.Length > 0
        && !name.StartsWith('.')
        && !name.Contains("..", StringComparison.Ordinal)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>压缩包资产名 → 解压目录名：去 .tar.gz/.tar.xz 扩展，再去资产名携带的架构后缀
    /// （dwproton-11.0-12-x86_64.tar.xz → dwproton-11.0-12，与 GE/UMU 的版本目录命名对齐）。</summary>
    private static string ProtonExtractDirectoryName(string assetName)
    {
        var stem = AssetStem(assetName);
        var arch = AssetArchSuffix(stem);
        return arch is null ? stem : stem[..^arch.Length];
    }

    /// <summary>发布资产名里可能携带的架构后缀。</summary>
    private static readonly string[] ArchSuffixes = ["-x86_64", "-aarch64"];

    /// <summary>
    /// proton.lock 互斥下的落位流程：已就绪且架构相符直接返回（架构不符视同缺失，走重装覆盖）；
    /// 否则下载归档 → 解包 → 校验目录布局 → 校验 wineserver 架构 → 补执行位，归档无论成败用后即删。
    /// tagName 仅用于日志（按 tag 下载时记录版本名）。返回 Proton 绝对目录。
    /// </summary>
    private async Task<string> InstallProtonAssetAsync(
        (string Name, string Url) asset,
        string targetDir,
        string tarPath,
        string? tagName,
        CancellationToken cancellationToken)
    {
        using (UmuPrefix.AcquireLock(UmuPaths.LockFile("proton.lock")))
        {
            if (IsProtonReady(targetDir) && MatchesHostArch(targetDir))
            {
                return Path.GetFullPath(targetDir);
            }

            try
            {
                await downloader.DownloadFileAsync(
                    new DownloadRequest(asset.Url, tarPath, ExpectedSize: null, ExpectedMd5: null),
                    null,
                    cancellationToken).ConfigureAwait(false);

                // release 资产可能是 .tar.gz；SharpCompress 可直接读 gzip+tar
                ExtractSingleTopLevel(tarPath, targetDir);

                if (!IsProtonReady(targetDir))
                {
                    throw new LaunchException(
                        LaunchFailureKind.ProtonDownloadFailed,
                        loc.Format("umu_err_packageIncomplete", asset.Name));
                }

                // 兜底校验：无架构后缀的资产也可能装错架构（wineserver 的 ELF e_machine 对照主机）
                var machine = ReadWineserverElfMachine(targetDir);
                if (machine is not null && ExpectedElfMachine is { } expected && machine != expected)
                {
                    TryDeleteDirectory(targetDir);
                    throw new LaunchException(
                        LaunchFailureKind.ProtonDownloadFailed,
                        loc.Format("umu_err_archMismatch", asset.Name, ArchDisplayName(machine.Value)));
                }

                TryChmod(Path.Combine(targetDir, "proton"));
                if (tagName is null)
                {
                    logger?.LogInformation("Installed Proton to {Dir}", targetDir);
                }
                else
                {
                    logger?.LogInformation("Installed Proton {Tag} to {Dir}", tagName, targetDir);
                }

                return Path.GetFullPath(targetDir);
            }
            catch (LaunchException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                or ArchiveException or InvalidDataException or FormatException
                or InvalidOperationException or DownloadException)
            {
                // DownloadException 必须在此分类：网络瞬断重试耗尽时下载器抛它，
                // 漏掉会让 VM 收到 Unknown，丢失重试按钮与本机 Proton 下拉的修复 UI
                throw new LaunchException(
                    LaunchFailureKind.ProtonDownloadFailed,
                    loc.Format("umu_err_protonDownload", ex.Message),
                    ex);
            }
            finally
            {
                TryDelete(tarPath);
            }
        }
    }

    /// <summary>从官方镜像下载指定变体的 Steam Runtime：解析版本号 → SHA256SUMS/BUILD_ID → 下载校验 →
    /// 暂存目录解包 → 原子落位并写安装标记。</summary>
    private async Task DownloadRuntimeAsync(
        string variant,
        string runtimeName,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var info = SteamRuntimeCatalog.ByAppId.Values
            .FirstOrDefault(r => string.Equals(r.Variant, variant, StringComparison.Ordinal))
            ?? new SteamRuntimeInfo(runtimeName, variant, "", "x86_64");

        var images = SteamRuntimeCatalog.ImagesPathPrefix(info);
        var archive = SteamRuntimeCatalog.ArchiveFileName(info);
        string? archivePath = null;
        try
        {
            try
            {
                // 版本号/SHA256SUMS/BUILD_ID 的裸 HttpRequestException 与下载器的 DownloadException
                // 统一转 UmuRuntimeDownloadFailed：漏网的会在 VM 落 Unknown，丢失重试修复 UI
                var version = (await FetchTextAsync(
                    $"{RuntimeHost}{images}/latest-public-beta.txt", cancellationToken).ConfigureAwait(false)).Trim();
                if (version.Length == 0)
                {
                    throw new LaunchException(
                        LaunchFailureKind.UmuRuntimeDownloadFailed,
                        loc["umu_err_runtimeVersionEmpty"]);
                }

                var baseUrl = $"{RuntimeHost}{images}/{version}";
                var sums = await FetchTextAsync($"{baseUrl}/SHA256SUMS", cancellationToken).ConfigureAwait(false);
                var expectedSha = ParseSha256For(sums, archive);
                var buildId = (await FetchTextAsync($"{baseUrl}/BUILD_ID.txt", cancellationToken).ConfigureAwait(false)).Trim();

                var cache = UmuPaths.CacheRoot(cacheHome);
                Directory.CreateDirectory(cache);
                archivePath = Path.Combine(cache, $"{archive}.{buildId}");
                progress?.Report(loc.Format("umu_progress_downloadRuntimeVersion", version));

                await downloader.DownloadFileAsync(
                    new DownloadRequest($"{baseUrl}/{archive}", archivePath, ExpectedSize: null, ExpectedMd5: null),
                    null,
                    cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(expectedSha))
                {
                    await VerifySha256Async(archivePath, expectedSha, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is DownloadException or HttpRequestException)
            {
                // 下载器网络重试耗尽抛 DownloadException（含校验失败的 DownloadVerificationException）；
                // 版本号拉取等直连请求抛 HttpRequestException——用户取消不在此分类（下方 rethrow）
                throw new LaunchException(
                    LaunchFailureKind.UmuRuntimeDownloadFailed,
                    loc.Format("umu_err_runtimeDownload", ex.Message),
                    ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // 直连版本号/SHA256SUMS 的连接或响应头超时（token 未取消的 TCE）——与 Proton 下载同款
                // 语义：超时必须按可重试失败分类，裸 OCE 上抛会被设置页的取消豁免静默吞、
                // 启动路径则落 Unknown 类目（2026-09-20 复审修复）
                throw new LaunchException(
                    LaunchFailureKind.UmuRuntimeDownloadFailed,
                    loc.Format("umu_err_runtimeDownloadTimeout", ex.Message),
                    ex);
            }
        }
        catch (Exception)
        {
            // 首段任何失败（含 SHA 校验的 LaunchException 与上面的两类转换结果）：归档/半成品
            // 不得滞留缓存——第二段的清理 finally 挂在解压段上，首段异常直接穿透它（F8，
            // 2026-09-24 前数百 MB 垃圾留到用户手动清理；重试虽覆写同路径，不该依赖用户行为）
            if (!string.IsNullOrEmpty(archivePath))
            {
                TryDelete(archivePath);
            }

            throw;
        }

        var installRoot = UmuPaths.RuntimeDirectory(variant, dataHome);
        var staging = installRoot + ".staging";
        if (Directory.Exists(staging))
        {
            // 上次中断的暂存树尽力清理（被占用时整删会抛，误报成下载失败）
            FileUtilities.TryDeleteDirectory(staging);
        }

        Directory.CreateDirectory(staging);
        try
        {
            ExtractTarArchive(archivePath, staging);
            // tar 内顶层目录 SteamLinuxRuntime_* → 挪到 installRoot
            var top = Directory.EnumerateDirectories(staging).FirstOrDefault()
                      ?? throw new LaunchException(
                          LaunchFailureKind.UmuRuntimeDownloadFailed,
                          loc["umu_err_runtimeNoTopDir"]);
            if (Directory.Exists(installRoot))
            {
                Directory.Delete(installRoot, recursive: true);
            }

            Directory.Move(top, installRoot);
            File.WriteAllText(Path.Combine(installRoot, UmuPaths.InstallMarkerName), DateTime.UtcNow.ToString("O"));

            var entry = Path.Combine(installRoot, "_v2-entry-point");
            if (File.Exists(entry))
            {
                TryChmod(entry);
                var umuLink = Path.Combine(installRoot, "umu");
                if (!File.Exists(umuLink) && !Directory.Exists(umuLink))
                {
                    try
                    {
                        File.CreateSymbolicLink(umuLink, "_v2-entry-point");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        // 忽略
                    }
                }
            }

            logger?.LogInformation("Installed Steam Runtime {Variant} to {Dir}", variant, installRoot);
        }
        catch (LaunchException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArchiveException
            or InvalidDataException or FormatException or InvalidOperationException)
        {
            throw new LaunchException(
                LaunchFailureKind.UmuRuntimeDownloadFailed,
                loc.Format("umu_err_runtimeExtract", ex.Message),
                ex);
        }
        finally
        {
            TryDelete(archivePath);
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>
    /// 解包 tar / tar.gz / tar.xz 到目标目录。条目解析与落盘走 System.Formats.Tar，
    /// 按 tar 头还原 Unix 权限位（SharpCompress 的解压不保留执行位，Proton/Runtime 树离开执行位无法启动）；
    /// 符号链接按原样创建，硬链接条目无数据、目标已解出时退化为符号链接。失败条目静默跳过（同 tar -x 容错），
    /// 拒绝绝对路径与 ".." 穿越。xz 容器仍用 SharpCompress 的 XZStream（BCL 无 XZ 解码）。
    /// </summary>
    internal static void ExtractTarArchive(string archivePath, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        using var raw = File.OpenRead(archivePath);
        using var decompressed = OpenDecompressedStream(raw);
        using var reader = new TarReader(decompressed);
        while (reader.GetNextEntry() is { } entry)
        {
            var key = entry.Name.Replace('\\', '/');
            // 路径穿越防护（IsPathRooted 连 Windows 盘符形态一并拒绝）
            if (Path.IsPathRooted(key) || key.Split('/').Contains(".."))
            {
                continue;
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(Path.Combine(destinationDir, key));
                    break;
                case TarEntryType.RegularFile:
                    WriteRegularFile(destinationDir, entry, key);
                    break;
                case TarEntryType.SymbolicLink:
                case TarEntryType.HardLink:
                    var link = entry.LinkName.Replace('\\', '/');
                    // 链接目标与条目名同等校验：绝对路径（跨平台口径含 Windows 盘符/UNC 形态——
                    // Linux 上 StartsWith('/') 挡不住 "C:/evil"，而该 tar 可能随后在 Windows 解出）
                    // 或 ".." 目标的链接可把后续普通文件条目经链接写穿到目标目录外（同上游包被
                    // 篡改时的纵深防御）
                    if (IsEscapingLinkTarget(link))
                    {
                        continue;
                    }

                    TryCreateLink(Path.Combine(destinationDir, key), entry.LinkName, entry.EntryType);
                    break;
            }
        }
    }

    /// <summary>tar 链接目标的穿越判定（纯函数，internal 供直测）：绝对路径（含 Windows
    /// 盘符 <c>C:/…</c> 与 UNC <c>//server/…</c> 形态，按跨平台口径而非当前主机）或含 ".." 段
    /// 即逃逸。F11（2026-09-24）：旧实现只挡 StartsWith('/')，盘符/UNC 目标在 Linux 上放行。</summary>
    internal static bool IsEscapingLinkTarget(string normalizedLink) =>
        normalizedLink.Length > 0 && normalizedLink[0] == '/'
        || (normalizedLink.Length >= 2
            && char.IsAsciiLetter(normalizedLink[0])
            && normalizedLink[1] == ':')
        || normalizedLink.Split('/').Contains("..");

    /// <summary>按魔数选择解压流：gzip → GZipStream，xz → XZStream，其余按未压缩 tar 原样透传。</summary>
    private static Stream OpenDecompressedStream(FileStream raw)
    {
        Span<byte> magic = stackalloc byte[6];
        var filled = 0;
        while (filled < magic.Length)
        {
            var read = raw.Read(magic[filled..]);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        raw.Position = 0;
        if (filled >= 2 && magic[0] == 0x1F && magic[1] == 0x8B)
        {
            return new GZipStream(raw, CompressionMode.Decompress);
        }

        if (filled >= 6 && magic[0] == 0xFD && magic[1] == 0x37 && magic[2] == 0x7A
            && magic[3] == 0x58 && magic[4] == 0x5A && magic[5] == 0x00)
        {
            return new XZStream(raw);
        }

        return raw;
    }

    /// <summary>落盘普通文件：tar 头权限位经 UnixCreateMode 原样还原（含执行位）；无权限信息时走 umask 默认。</summary>
    private static void WriteRegularFile(string destinationDir, TarEntry entry, string key)
    {
        var target = Path.Combine(destinationDir, key);
        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows() && entry.Mode != UnixFileMode.None)
        {
            options.UnixCreateMode = entry.Mode & AllPermissionBits;
        }

        using var output = new FileStream(target, options);
        entry.DataStream?.CopyTo(output);
    }

    /// <summary>
    /// 创建链接条目：符号链接按 tar 记录的原样重建；硬链接条目没有数据段，
    /// 目标已解出时以符号链接替代（同一份内容，保证可执行语义），否则跳过。
    /// 无链接权限（如 Windows 未开开发者模式）时静默跳过，不阻断整个解包。
    /// </summary>
    private static void TryCreateLink(string linkPath, string targetName, TarEntryType entryType)
    {
        var parent = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }

            if (entryType == TarEntryType.HardLink)
            {
                var linkDir = Path.GetDirectoryName(linkPath);
                var resolved = string.IsNullOrEmpty(linkDir) ? targetName : Path.Combine(linkDir, targetName);
                if (!File.Exists(resolved))
                {
                    return;
                }
            }

            File.CreateSymbolicLink(linkPath, targetName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

    /// <summary>tar 头里可用的九个权限位。</summary>
    private const UnixFileMode AllPermissionBits =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>internal 供单测（经 InternalsVisibleTo）：验证顶层目录迁移与“无顶层目录直接摊开”两种包布局。</summary>
    internal static void ExtractSingleTopLevel(string archivePath, string targetDir)
    {
        var temp = targetDir + ".extract";
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }

        Directory.CreateDirectory(temp);
        try
        {
            ExtractTarArchive(archivePath, temp);

            // GE-Proton 包顶层通常是一个目录；也可能直接摊开
            var top = Directory.EnumerateDirectories(temp).FirstOrDefault();
            var source = top ?? temp;
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, recursive: true);
            }

            Directory.Move(source, targetDir);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                try
                {
                    Directory.Delete(temp, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>在 compatibilitytools.d 下找某发行版前缀已装的最新版本（数字段自然序）；没有返回 null。</summary>
    private string? FindLatestLocalProton(string prefixOrCodename)
    {
        var root = UmuPaths.SteamCompatRoot(dataHome);
        if (!Directory.Exists(root))
        {
            return null;
        }

        var filter = MatchFlavor(prefixOrCodename)?.LocalPrefix;
        if (filter is null)
        {
            return null;
        }

        // 数字段自然序（单一事实源 CompatTools.NumericSortKey）：GE-Proton10-* 应排在 GE-Proton9-* 之前；
        // 架构不符的已装目录跳过（视同缺失，调用方走下载自愈）
        return Directory.EnumerateDirectories(root)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return IsProtonReady(d)
                    && MatchesHostArch(d)
                    && name.StartsWith(filter, StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(d => CompatTools.NumericSortKey(Path.GetFileName(d)), StringComparer.Ordinal)
            .Select(Path.GetFullPath)
            .FirstOrDefault();
    }

    /// <summary>是否为代号（DW/GE/UMU-Proton 及 *-Latest 变体）；单一来源在 CompatTools.IsProtonCodename。</summary>
    internal static bool IsCodename(string value) => CompatTools.IsProtonCodename(value);

    /// <summary>GET 一个文本响应（带 User-Agent）；非 2xx 由 EnsureSuccessStatusCode 抛 HttpRequestException。</summary>
    private async Task<string> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("YetAnotherGameLauncher");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>从 SHA256SUMS 文本里解析目标文件名的摘要。</summary>
    public static string ParseSha256For(string sumsText, string fileName)
    {
        foreach (var line in sumsText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.EndsWith(fileName, StringComparison.Ordinal))
            {
                return trimmed.Split(' ', '\t')[0].Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>校验下载的 Steam Runtime 包 SHA256，不符抛 LaunchException（启动失败覆盖层展示）。</summary>
    private async Task VerifySha256Async(string path, string expected, CancellationToken cancellationToken)
    {
        var actual = await Hashing.Sha256HexAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expected.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new LaunchException(
                LaunchFailureKind.UmuRuntimeDownloadFailed,
                loc.Format("umu_err_runtimeShaMismatch", expected, actual));
        }
    }

    private static void TryChmod(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
