using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Channels.Hypergryph.Models;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Channels.Hypergryph;

/// <summary>
/// 鹰角/GRYPHLINE 渠道（明日方舟：终末地）。
/// 整包分发模型：get_latest_game 返回压缩包列表（packs），下载解压即安装；
/// 更新 = 请求新版本的包并解压覆盖；预下载 = 响应中的 patch 节点。
///
/// ⚠ 该协议无官方文档，字段来自社区逆向（LLauncher / ak-endfield-api-archive），
/// 可能随官方启动器更新而变化；协议参数由 games.json 的 server.options 提供，
/// 缺省回退国际服（launcher.gryphline.com）实测值。
/// 国服：apiBase=https://launcher.hypergryph.com/api、appcode=6LL0KJuqHBVz33WK、
/// channel=1、subChannel=1；B 服 channel=2、subChannel=2（2026-09 实测，响应结构与国际服一致）。
/// packs 为同一归档的字节分卷（…zip.001…059，2026-10-17 国服/国际服 1.6.4 实测），
/// 置 <see cref="GameManifest.ArchivesAreSplitVolumes"/> 并排序后交安装器拼接解压。
/// </summary>
public sealed class GryphlineChannelApi(HttpClient httpClient, ILogger? logger = null) : IGameChannelApi
{
    public const string AppcodeOptionKey = "appcode";
    public const string ChannelOptionKey = "channel";
    public const string SubChannelOptionKey = "subChannel";

    // 国际服（osWinRel）实测参数，作为 options 未提供时的缺省值（背景接口等复用）
    public const string DefaultGameAppcode = "YDUTE5gscDZ229CW";

    private const string ApiBaseOptionKey = GryphlineProtocol.ApiBaseOptionKey;

    /// <summary>官方补丁包内的废弃文件清单条目名（delete_files.txt）。</summary>
    private const string ObsoleteListEntryName = "delete_files.txt";

    /// <summary>v2 载荷里差分条目的前缀（清单的 patch 字段相对它，2026-10-17 实测）。</summary>
    private const string DeltaPatchEntryPrefix = "vfs_files/vfs_patch/";

    /// <summary>v2 载荷里不属于游戏内容的条目前缀（差分与 VFS 节点都不走"静态整文件"复制）。</summary>
    private static readonly string[] DeltaStaticExcludedPrefixes = ["vfs_files/", "diff_"];

    /// <summary>v2 载荷里的清单类条目（不是游戏文件，不落位）。</summary>
    private static readonly string[] DeltaStaticExcludedNames =
        ["patch.json", "delete_files.txt", "game_files", "package_files", "verify_files", "verify_files.json"];

    /// <summary>v2 载荷静态条目的目标改写：官方把 config.ini 写成 config.ini.new
    /// （社区参考实现同款行为，2026-10-17 沿用；真机验收时观察游戏是否自行接管）。</summary>
    private static readonly Dictionary<string, string> DeltaStaticRewrites = new(StringComparer.Ordinal)
    {
        ["config.ini"] = "config.ini.new",
    };
    public const string DefaultChannelId = "6";
    public const string DefaultSubChannelId = "9999";

    public async Task<ChannelVersionInfo> GetVersionInfoAsync(GameServer server, CancellationToken cancellationToken = default)
    {
        var response = await PostGetLatestGameAsync(server, cancellationToken).ConfigureAwait(false);

        string? predownloadVersion = null;
        if (response.Patch is { ValueKind: JsonValueKind.Object } patch
            && patch.TryGetProperty("version", out var versionElement)
            // 逆向协议、结构随官方改动：GetString 对非字符串值抛裸 InvalidOperationException
            //（本渠道其余解析处均有 ValueKind 防护，此处补齐）
            && versionElement.ValueKind == JsonValueKind.String
            && versionElement.GetString() is { Length: > 0 } v)
        {
            predownloadVersion = v;
        }

        logger?.LogDebug("GRYPHLINE latest: {Version} (predownload: {Predownload})", response.Version, predownloadVersion ?? "none");

        return new ChannelVersionInfo
        {
            LatestVersion = response.Version,
            PatchSourceVersions = [], // 包式渠道：无按版本差分入口，更新走整包
            PredownloadAvailable = predownloadVersion is not null,
            PredownloadVersion = predownloadVersion,
            PredownloadPatchSourceVersions = [],
        };
    }

    public async Task<GameManifest> GetManifestAsync(
        GameServer server, string version, string? resourceTier = null, CancellationToken cancellationToken = default)
    {
        // 包式渠道只能取最新整包清单：version 参数仅用于接口契约对齐，响应版本即目标版本；
        // 档位（鸣潮专属）不适用，忽略
        var response = await PostGetLatestGameAsync(server, cancellationToken).ConfigureAwait(false);
        return ToPackageManifest(response.Version, response.Pkg!);
    }

    public Task<GameManifest?> GetIncrementalManifestAsync(
        GameServer server, string fromVersion, string toVersion, string? resourceTier = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<GameManifest?>(null); // 包式渠道：更新走整包，无按版本差分清单

    public async Task<GameManifest?> GetPredownloadManifestAsync(GameServer server, CancellationToken cancellationToken = default)
    {
        var response = await PostGetLatestGameAsync(server, cancellationToken).ConfigureAwait(false);
        if (response.Patch is not { } patch || patch.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var patchVersion = patch.TryGetProperty("version", out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (patchVersion is null || !patch.TryGetProperty("pkg", out var pkgElement) || pkgElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // patch.pkg 形态漂移（协议逆向威胁模型）在此折算：非目标形态抛裸 JsonException、
        // "packs": null 覆盖 =[] 初始化器后 pkg.Packs 裸解引用 NRE——都不属于可折算的上层
        // catch（:142 折算只包 batch envelope），会穿成无分类技术文案（F40，6664197 同纪律）
        PackageInfo? pkg;
        try
        {
            pkg = pkgElement.Deserialize<PackageInfo>(GryphlineProtocol.JsonOptions);
        }
        catch (JsonException)
        {
            return null; // 调用方已有"无本版本预下载补丁"分支接住 null
        }

        return pkg is null || pkg.Packs is not { Count: > 0 }
            ? null
            : ToPackageManifest(patchVersion, pkg);
    }

    /// <summary>
    /// 官方点名的废弃文件清单（本渠道 = 补丁包内的 <c>delete_files.txt</c>，实测 324 条 /
    /// 47.72 GiB 旧 VFS 分块；2026-10-17 真机实证）：带本地版本请求时 patch.url 指向
    /// "本地版本 → 最新版本"的更新补丁，清单在其中。补丁包单文件形态约 49 GiB，清单经
    /// <see cref="RemoteZipEntryReader"/> 的 Range 分段读取取出（绝不整包下载）。
    /// 取不到（无补丁节点/服务器已前移/网络与形态失败）一律返回 null：清理是尽力而为，
    /// 不得让更新失败——但失败原因记日志保持可诊断。
    /// </summary>
    public async Task<IReadOnlyList<string>?> GetObsoleteFilesAsync(
        GameServer server, string fromVersion, string toVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fromVersion)
            || string.IsNullOrEmpty(toVersion)
            || string.Equals(fromVersion, toVersion, StringComparison.Ordinal))
        {
            return null; // 无版本过渡即无废弃清单（也已避免一次无意义的请求）
        }

        try
        {
            var response = await PostGetLatestGameAsync(server, cancellationToken, fromVersion).ConfigureAwait(false);
            // 版本守卫（Kuro dst 解析缝同族）：清单接口只服务"当前最新"，服务器已前移时
            // 该过渡的补丁可能已下架或换了内容，按拿不到清单处理
            if (!string.Equals(response.Version, toVersion, StringComparison.Ordinal))
            {
                logger?.LogInformation(
                    "Obsolete file list skipped: server serves {Served}, target was {Target}.", response.Version, toVersion);
                return null;
            }

            if (response.Patch is not { ValueKind: JsonValueKind.Object } patch
                || !patch.TryGetProperty("url", out var urlElement)
                || urlElement.ValueKind != JsonValueKind.String
                || urlElement.GetString() is not { Length: > 0 } patchUrl)
            {
                return null;
            }

            var content = await RemoteZipEntryReader
                .TryReadEntryAsync(httpClient, patchUrl, ObsoleteListEntryName, logger, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                return null;
            }

            var obsolete = System.Text.Encoding.UTF8.GetString(content)
                .Split('\n')
                .Select(line => line.Trim('\r', ' ', '\t'))
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            logger?.LogInformation(
                "Obsolete file list for {From} -> {To}: {Count} entries.", fromVersion, toVersion, obsolete.Length);
            return obsolete;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Failed to fetch the obsolete file list for {From} -> {To}.", fromVersion, toVersion);
            return null;
        }
    }

    /// <summary>
    /// v2 增量更新计划（终末地实测形态）：带本地版本请求 get_latest_game，patch 节点给出一条
    /// "本地版本 → 最新版本"的增量载荷（9 卷 ≈8.72 GiB，对比整包 58 GiB）与明文清单
    /// （v2_patch_info_url）；清单逐文件给出目标 md5/size 与差分基文件。
    /// 无法给出计划（无补丁节点/字段缺失/服务器已前移/分卷集异常/网络失败）返回 null，
    /// 由调用方回退整包更新——这里不抛错（整包永远可用）。
    /// </summary>
    public async Task<DeltaPackagePlan?> GetDeltaPackageAsync(
        GameServer server, string fromVersion, string toVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fromVersion)
            || string.IsNullOrEmpty(toVersion)
            || string.Equals(fromVersion, toVersion, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var response = await PostGetLatestGameAsync(server, cancellationToken, fromVersion).ConfigureAwait(false);
            if (response.Patch is not { ValueKind: JsonValueKind.Object } patch)
            {
                return null;
            }

            // 目标匹配（复核 P1，2026-10-18）：patch 节点描述"本地版本 → patch.version"的补丁。
            // 更新期 patch.version == response.version（线上版本），两条件等价；但**预下载窗口期**
            // response.version 仍是线上版本、patch.version 才是预下载目标（窗口形态见
            // GetVersionInfo_PredownloadAvailable_WhenPatchHasVersion 夹具与 Kuro 真机 predownload 块）——
            // 只认 response.version 会让窗口期的预下载计划恒取不到、静默退回整包。任一命中即视为
            // 该补丁属于请求目标；清单 version 逐字符核对（下方 manifest.Version）仍是内容兜底。
            var patchVersion = patch.TryGetProperty("version", out var versionElement)
                && versionElement.ValueKind == JsonValueKind.String ? versionElement.GetString() : null;
            if (!string.Equals(response.Version, toVersion, StringComparison.Ordinal)
                && !string.Equals(patchVersion, toVersion, StringComparison.Ordinal))
            {
                logger?.LogInformation(
                    "Delta package skipped: server serves {Served}, patch targets {Patch}, requested {Target}.",
                    response.Version, patchVersion ?? "none", toVersion);
                return null;
            }

            EndfieldPatchNode? node;
            try
            {
                node = patch.Deserialize<EndfieldPatchNode>(GryphlineProtocol.JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }

            if (node is null
                || string.IsNullOrEmpty(node.CdKey)
                || node.Patches is not { Count: > 0 } volumeDtos
                || string.IsNullOrEmpty(node.V2PatchInfoUrl))
            {
                return null;
            }

            var (kind, order) = SplitVolumeNaming.Classify([.. volumeDtos.Select(v => VolumeName(v.Url))]);
            if (kind != SplitVolumeSet.Ordered)
            {
                logger?.LogWarning("Delta package skipped: payload volumes are not a contiguous split set.");
                return null;
            }

            var volumes = order.Select(index => volumeDtos[index])
                .Select(v => new DeltaPackageVolume(v.Url, ParseSize(v.PackageSize), v.Md5))
                .ToArray();

            using var manifestResponse = await httpClient.GetAsync(node.V2PatchInfoUrl, cancellationToken).ConfigureAwait(false);
            if (!manifestResponse.IsSuccessStatusCode)
            {
                logger?.LogWarning("Delta manifest fetch failed with {Status}.", (int)manifestResponse.StatusCode);
                return null;
            }

            var manifest = await manifestResponse.Content
                .ReadFromJsonAsync<EndfieldPatchManifest>(GryphlineProtocol.JsonOptions, cancellationToken).ConfigureAwait(false);
            if (manifest?.Files is not { Count: > 0 } manifestFiles
                || string.IsNullOrEmpty(manifest.VfsBasePath)
                || !string.Equals(manifest.Version, toVersion, StringComparison.Ordinal))
            {
                return null;
            }

            var files = manifestFiles.Select(file => new DeltaPackageFile(
                Name: file.Name,
                Md5: file.Md5,
                Size: file.Size,
                DiffType: file.DiffType,
                FullFilePayloadPath: string.IsNullOrEmpty(file.LocalPath) ? null : file.LocalPath,
                Patches: [.. (file.Patches ?? []).Select(p => new DeltaPatchSource(
                    BaseFile: p.BaseFile,
                    BaseMd5: p.BaseMd5,
                    BaseSize: p.BaseSize,
                    PatchPath: DeltaPatchEntryPrefix + p.Patch,
                    PatchSize: p.PatchSize))])).ToArray();

            logger?.LogInformation(
                "Delta package for {From} -> {To}: {Volumes} volumes ({Bytes} bytes), {Files} file nodes.",
                fromVersion, toVersion, volumes.Length, volumes.Sum(v => v.Size), files.Length);
            return new DeltaPackagePlan(
                FromVersion: fromVersion,
                ToVersion: toVersion,
                VfsBasePath: manifest.VfsBasePath,
                CdKey: node.CdKey,
                Volumes: volumes,
                Files: files,
                StaticEntryExcludedPrefixes: DeltaStaticExcludedPrefixes,
                StaticEntryExcludedNames: DeltaStaticExcludedNames,
                StaticEntryRewrites: DeltaStaticRewrites);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Delta package resolution failed for {From} -> {To}; falling back to the full package.", fromVersion, toVersion);
            return null;
        }
    }

    private static long ParseSize(string value) => long.TryParse(value, out var size) ? size : 0;

    /// <summary>载荷条目名（URL 末段）。</summary>
    private static string VolumeName(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Segments.Length > 0
            ? Uri.UnescapeDataString(uri.Segments[^1].TrimEnd('/'))
            : url;

    /// <summary>
    /// 发一次 batch_proxy get_latest_game。<paramref name="version"/> = 客户端当前版本（官方语义）：
    /// 留空取"最新版本 + 预下载 patch 节点"，填本地版本时 patch 节点变成"从该版本出发的更新补丁"。
    /// </summary>
    private async Task<GameVersionResponse> PostGetLatestGameAsync(
        GameServer server, CancellationToken cancellationToken, string version = "")
    {
        var apiBase = server.Options.TryGetValue(ApiBaseOptionKey, out var apiBaseValue)
            && !string.IsNullOrWhiteSpace(apiBaseValue)
            ? apiBaseValue.TrimEnd('/')
            : throw new UpdateException($"Server \"{server.Name}\" is missing the {ApiBaseOptionKey} option.");

        var payload = new BatchProxyRequest
        {
            ProxyReqs =
            [
                new BatchProxyReq
                {
                    Kind = "get_latest_game",
                    GetLatestGameReq = new GetLatestGameReq
                    {
                        Version = version,
                        Appcode = GryphlineProtocol.OptionOrDefault(server.Options, AppcodeOptionKey, DefaultGameAppcode),
                        Channel = GryphlineProtocol.OptionOrDefault(server.Options, ChannelOptionKey, DefaultChannelId),
                        SubChannel = GryphlineProtocol.OptionOrDefault(server.Options, SubChannelOptionKey, DefaultSubChannelId),
                        DeviceId = "yetanothervariant-game-launcher",
                    },
                },
            ],
        };

        logger?.LogDebug("POST {Url}", $"{apiBase}/proxy/batch_proxy");

        GameVersionResponse gameResponse;
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"{apiBase}/proxy/batch_proxy", payload, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var batch = await response.Content.ReadFromJsonAsync<BatchProxyResponse>(cancellationToken).ConfigureAwait(false);
            var first = batch?.ProxyRsps is { Count: > 0 } rsps
                ? rsps[0]
                : throw new UpdateException("GRYPHLINE batch_proxy response is empty.");

            // 包裹键缺失/元素非对象时 GetProperty 会抛 KeyNotFoundException/InvalidOperationException，
            // 不属于 JsonException、会穿出上层离线兜底的 catch 过滤器（EndfieldBackdropResolver 同纪律：
            // 协议逆向、错误响应可能不带目标键，必须折算成 UpdateException）
            gameResponse = first.ValueKind == JsonValueKind.Object
                           && first.TryGetProperty("get_latest_game_rsp", out var rspElement)
                           && rspElement.ValueKind == JsonValueKind.Object
                ? rspElement.Deserialize<GameVersionResponse>(GryphlineProtocol.JsonOptions)
                    ?? throw new UpdateException("GRYPHLINE get_latest_game_rsp is empty.")
                : throw new UpdateException("GRYPHLINE get_latest_game_rsp is missing or not an object.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or NotSupportedException)
        {
            // 协议逆向威胁模型下的三形态统一折算（F60）：畸形 JSON → JsonException（含 200+HTML
            // 的 WAF 页——.NET 10 实测 ReadFromJsonAsync 不校验 content-type，HTML 走解析失败路径）；
            // 畸形 apiBase 在 PostAsJsonAsync 构造请求时抛 UriFormatException（FormatException）；
            // NotSupportedException 是文档记载的非 JSON content-type 形态（.NET 10 探针未复现，
            // 显式收留防运行时行为回摆）
            throw new UpdateException($"Failed to parse GRYPHLINE response: {ex.Message}", ex);
        }

        if (gameResponse.Pkg is null)
        {
            throw new UpdateException("GRYPHLINE response is missing pkg (package info).");
        }

        // 版本缺失即拒收：空版本一旦被登记/落盘为本地版本，IsNewer 恒判"无更新"，
        // 该游戏此后永久失去更新检测且无自愈路径（协议逆向、字段随官方改动，
        // 缺失时宁可报错也不能静默毒化状态）（2026-09-20 复审修复）
        if (string.IsNullOrEmpty(gameResponse.Version))
        {
            throw new UpdateException("GRYPHLINE response is missing version.");
        }

        return gameResponse;
    }

    private static GameManifest ToPackageManifest(string version, PackageInfo pkg)
    {
        // 空/缺失 packs = 无内容可装的清单：收下它会走"空转下载→按废弃清单清理"（review P2-2 的源头），
        // 与预下载路径同款拒收（F40 同族）
        if (pkg.Packs is not { Count: > 0 } packs)
        {
            throw new UpdateException("GRYPHLINE response has no package entries (pkg.packs is empty).");
        }

        var files = new List<ManifestFile>(packs.Count);
        for (var i = 0; i < packs.Count; i++)
        {
            var pack = packs[i];
            files.Add(new ManifestFile(
                Path: GetFileNameFromUrl(pack.Url) ?? $"package-{i}.bin",
                Size: long.TryParse(pack.PackageSize, out var size) ? size : 0,
                Md5: pack.Md5,
                Url: pack.Url));
        }

        var archivesAreSplitVolumes = TryOrderAsSplitVolumes(files, static file => file.Path);
        return new GameManifest
        {
            Version = version,
            EntriesAreArchives = true,
            ArchivesAreSplitVolumes = archivesAreSplitVolumes,
            Files = files,
        };
    }

    /// <summary>
    /// packs 条目按分卷规则就序：Ordered 时按序号重排并返回 true；NotVolumes 保持原样返回 false；
    /// Broken 抛 UpdateException 提前拒收（拼接结果必然不是合法归档，否则用户要先下几十 GiB 才在
    /// 解压阶段撞上无分类的 EOCD 报错）。
    /// </summary>
    private static bool TryOrderAsSplitVolumes(List<ManifestFile> files, Func<ManifestFile, string> nameSelector)
    {
        var (kind, order) = SplitVolumeNaming.Classify([.. files.Select(nameSelector)]);
        if (kind == SplitVolumeSet.NotVolumes)
        {
            return false;
        }

        if (kind == SplitVolumeSet.Broken)
        {
            throw new UpdateException(
                "GRYPHLINE packs are not a contiguous single set of split volumes: " +
                string.Join(", ", files.Select(f => f.Path)));
        }

        var reordered = order.Select(index => files[index]).ToList();
        files.Clear();
        files.AddRange(reordered);
        return true;
    }

    private static string? GetFileNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var last = Uri.UnescapeDataString(uri.Segments[^1].TrimEnd('/'));
        return last.Length == 0 ? null : last;
    }
}
