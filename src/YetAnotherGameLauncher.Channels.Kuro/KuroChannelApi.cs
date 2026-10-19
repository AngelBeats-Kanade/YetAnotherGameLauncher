using System.Text.Json;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Channels.Kuro.Models;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>
/// 库洛游戏渠道（鸣潮官服/B 服/国际服）。
/// indexUrl 由 games.json 的 server.options 提供，代码不内置任何服务器地址。
/// </summary>
public sealed class KuroChannelApi(IDownloader downloader, ILogger? logger = null) : IGameChannelApi
{
    public const string IndexUrlOptionKey = "indexUrl";

    /// <summary>官方档位索引地址（resourcePacks；与 indexUrl 同源同 hash 约定，不含版本号）。
    /// 缺省 = 该服务器不支持按档位下载（档位只作启动参数）。</summary>

    public const string ResourcePackIndexUrlOptionKey = "resourcePackIndexUrl";

    public bool SupportsResourcePackDownload(GameServer server) =>
        server.Options.TryGetValue(ResourcePackIndexUrlOptionKey, out var url) && !string.IsNullOrWhiteSpace(url);

    public async Task<ChannelVersionInfo> GetVersionInfoAsync(GameServer server, CancellationToken cancellationToken = default)
    {
        var index = await FetchIndexAsync(server, cancellationToken).ConfigureAwait(false);
        var block = RequireDefault(index);

        var predownloadConfig = index.Predownload?.Config;
        return new ChannelVersionInfo
        {
            LatestVersion = RequireVersion(block),
            PatchSourceVersions = GetPatchSourceVersions(block.Config),
            // 预下载可用性按官方契约 = predownloadSwitch 开启且 predownload 块带 config；
            // 官方关闭开关但残留 predownload 块时不得误报可预下载（随后增量清单查找必失败）
            PredownloadAvailable = predownloadConfig is not null && index.PredownloadSwitch == 1,
            PredownloadVersion = predownloadConfig?.Version,
            PredownloadPatchSourceVersions = GetPatchSourceVersions(predownloadConfig),
        };
    }

    public async Task<GameManifest> GetManifestAsync(
        GameServer server, string version, string? resourceTier = null, CancellationToken cancellationToken = default)
    {
        var index = await FetchIndexAsync(server, cancellationToken).ConfigureAwait(false);
        var block = RequireDefault(index);
        var cdn = RequireCdn(block);
        var config = RequireConfig(block);

        var indexFileJson = await FetchTextAsync(
            KuroUrlBuilder.BuildFileUrl(cdn, null, RequireIndexFile(config)),
            config.IndexFileMd5,
            cancellationToken).ConfigureAwait(false);
        var indexFile = ParseJson<KuroIndexFile>(indexFileJson, "indexFile.json");

        // 档位（2026-10-19）：未设置的生效档 = hd。官方档位索引的 hd 包与标准清单的 HD 条目
        // 逐字节一致（2026-10-19 端点逐条对照实测），故 hd/未设置一律走标准清单、不额外联网；
        // sd/uhd 才取官方档位索引：裁掉其它档位目录 + 追加目标档位包。
        // 服务器无档位索引（国际服/B 服实测 404 或用户未配）时档位归一为 hd：内容恒为官方默认
        // bundle（common + HD），档位只作启动参数（UI 侧按 SupportsResourcePackDownload 提示）。
        var tier = SyncTier(server, resourceTier);
        if (tier == ResourceTiers.Hd)
        {
            return new GameManifest
            {
                Version = version,
                Files = ToManifestFiles(indexFile.Resource, cdn, config.BaseUrl ?? block.ResourcesBasePath),
                Groups = ToGroups(indexFile.GroupInfos, cdn, patchBaseUrl: null, config.BaseUrl, block.ResourcesBasePath),
                ResourceTier = ResourceTiers.Hd,
                PreservedPaths = PreservedTierDirs(ResourceTiers.Hd),
            };
        }

        var packIndex = await FetchResourcePackIndexAsync(server, cancellationToken).ConfigureAwait(false);
        var pack = RequireResourcePack(packIndex, tier);
        // 档位索引的节点在顶层下发（包配置块无 cdnList，2026-10-16 实测）；顶层无可用节点回退标准块
        var packCdn = KuroCdnSelector.SelectCdn(packIndex.CdnList) ?? cdn;
        var packIndexFile = await FetchPackIndexFileAsync(pack, packCdn, tier, cancellationToken).ConfigureAwait(false);
        var packFiles = ToManifestFiles(packIndexFile.Resource, packCdn, pack.BaseUrl);

        // 包清单空 = 拉到了残缺/异常响应的信号：绝不能返回"缺目标档位包"的清单——同步收尾会把
        // 目标档位目录当游离文件删掉（几十 GB）。宁可拒收让用户重试（与 dstFiles 缺 url 的
        // "拒绝静默降级"同哲学：官方在档位包上永远给 100 个条目）
        if (packFiles.Count == 0)
        {
            throw new UpdateException(
                $"Kuro resource pack \"{tier}\" lists no files; refusing to sync without it " +
                "(a pack-less manifest would delete the tier's local files as stale). Please retry.");
        }

        var otherTiers = ResourceTiers.OtherTiers(tier);
        return new GameManifest
        {
            Version = version,
            Files =
            [
                .. ToManifestFiles(
                    indexFile.Resource.Where(e => !otherTiers.Any(t => IsInTierDirectory(e.Dest, t))),
                    cdn, config.BaseUrl ?? block.ResourcesBasePath),
                .. packFiles,
            ],
            Groups = ToGroups(
                indexFile.GroupInfos?.Where(g => !otherTiers.Any(t => IsInTierDirectory(g.Dest, t))),
                cdn, patchBaseUrl: null, config.BaseUrl, block.ResourcesBasePath),
            ResourceTier = tier,
            PreservedPaths = PreservedTierDirs(tier),
        };
    }

    public async Task<GameManifest?> GetIncrementalManifestAsync(
        GameServer server, string fromVersion, string toVersion, string? resourceTier = null, CancellationToken cancellationToken = default)
    {
        var index = await FetchIndexAsync(server, cancellationToken).ConfigureAwait(false);

        // 差分入口与目标版本同块：预下载（live → predownload）的差分入口在 predownload 块的
        // patchConfig 里，常规更新（旧 live → live）在 default 块；目标版本不属于任一块时
        // 回退 default 块。选中块查不到条目时再回退另一块查一次：官方切版本窗口期差分条目
        // 与目标版本可能不在同一块（如 default 已切到 V2 而 predownload 块残留且其 patchConfig
        // 被 CDN 清理），硬性返回 null 会把可用的增量更新推向全量重下（2026-09-20 复审修复）。
        var primary = index.Predownload?.Config is { } preConfig && preConfig.Version == toVersion
            ? index.Predownload!
            : RequireDefault(index);
        var secondary = ReferenceEquals(primary, index.Predownload) ? index.Default : index.Predownload;

        foreach (var block in new[] { primary, secondary })
        {
            if (block?.Config is not { } config)
            {
                continue;
            }

            var patchEntry = config.PatchConfig?.FirstOrDefault(p => p.Version == fromVersion);
            if (patchEntry?.IndexFile is null)
            {
                continue;
            }

            // cdnList 官方仅随 default 块下发、两块共用（2026-09-29 真机实测 predownload 块无
            // cdnList 字段）；选中块缺可用节点时回退 default 块，仍无才拒收
            var cdn = RequireCdn(block, index.Default);

            var patchIndexJson = await FetchTextAsync(
                KuroUrlBuilder.BuildFileUrl(cdn, null, patchEntry.IndexFile),
                patchEntry.IndexFileMd5,
                cancellationToken).ConfigureAwait(false);
            var patchIndexFile = ParseJson<KuroIndexFile>(patchIndexJson, "incremental indexFile.json");

            // 增量清单的 resource 列表同时登记直下文件与 krpdiff 差分文件（后者与 groupInfos[].dest
            // 同名、无 fromFolder，2026-10-02 真机实测 3.6.1→3.7.0：49 条 = 11 直下 + 38 krpdiff）。
            // krpdiff 只经 Groups 表达（URL = patchEntry.baseUrl 差分目录；留在 Files 会被套
            // fromFolder 前缀 zip/ → 真机 38/38 全 404，且下载两遍、apply 时被搬进游戏目录）——
            // 参考实现 ww-manager incremental.py 同语义（_is_diff_resource 过滤 complete_files）。
            //
            // 档位（2026-10-19）：sd/uhd 时裁掉其它档位目录的条目与差分组——档位包自身跨版本不变
            // （官方实测 hd/sd/uhd 的 3.7.0 → 3.7.1 增量清单为空：resource 与 deleteFiles 均空），
            // 其落盘由更新/应用后的全量修复轮按档位清单补齐（GameUpdateService.RepairAgainstManifestAsync）。
            var tier = SyncTier(server, resourceTier);
            var otherTiers = ResourceTiers.OtherTiers(tier);
            var directResources = patchIndexFile.Resource
                .Where(r => !IsDiffResource(r.Dest) && !otherTiers.Any(t => IsInTierDirectory(r.Dest, t)))
                .ToList();

            return new GameManifest
            {
                Version = toVersion,
                Files = ToManifestFiles(
                    directResources, cdn,
                    folder: FirstFromFolder(directResources) ?? patchEntry.BaseUrl ?? config.BaseUrl ?? block.ResourcesBasePath),
                Groups = ToGroups(
                    patchIndexFile.GroupInfos?.Where(g => !otherTiers.Any(t => IsInTierDirectory(g.Dest, t))),
                    cdn, patchEntry.BaseUrl, config.BaseUrl, block.ResourcesBasePath,
                    patchIndexFile.Resource),
                // 废弃清单同样裁掉其它档位目录（2026-10-19 review 实锤：原样透传会让 SD/UHD 更新
                // 删掉 HD 目录里清单点名的文件，与"切档不删旧档、删除只经显式清理"冲突）
                DeleteFiles =
                [
                    .. (patchIndexFile.DeleteFiles ?? [])
                        .Where(d => !otherTiers.Any(t => IsInTierDirectory(d, t))),
                ],
                ResourceTier = tier,
                PreservedPaths = PreservedTierDirs(tier),
            };
        }

        return null;
    }

    private async Task<KuroLauncherIndex> FetchIndexAsync(GameServer server, CancellationToken cancellationToken)
    {
        if (!server.Options.TryGetValue(IndexUrlOptionKey, out var indexUrl) || string.IsNullOrWhiteSpace(indexUrl))
        {
            throw new UpdateException($"Server \"{server.Name}\" is missing the {IndexUrlOptionKey} option.");
        }

        logger?.LogDebug("Fetching Kuro index.json: {Url}", indexUrl);
        var json = await FetchTextAsync(indexUrl, expectedMd5: null, cancellationToken).ConfigureAwait(false);
        return ParseJson<KuroLauncherIndex>(json, "index.json");
    }

    /// <summary>取官方档位索引（resourcePacks）。服务器未配档位索引即拒收——调用方须先用
    /// <see cref="SupportsResourcePackDownload"/> 分流（UI 面同缝）。</summary>
    private async Task<KuroLauncherIndex> FetchResourcePackIndexAsync(GameServer server, CancellationToken cancellationToken)
    {
        if (!server.Options.TryGetValue(ResourcePackIndexUrlOptionKey, out var url) || string.IsNullOrWhiteSpace(url))
        {
            throw new UpdateException($"Server \"{server.Name}\" is missing the {ResourcePackIndexUrlOptionKey} option.");
        }

        logger?.LogDebug("Fetching Kuro resource pack index: {Url}", url);
        var json = await FetchTextWithContextAsync(url, expectedMd5: null, "the resource pack index", cancellationToken)
            .ConfigureAwait(false);
        return ParseJson<KuroLauncherIndex>(json, "resource pack index.json");
    }

    /// <summary>取档位包清单文件（{pack indexFile}，md5 校验在下载器内完成）。
    /// 失败（网络/校验/JSON）折算带档位名的 UpdateException：档位索引是 2026-10-19 新引入的
    /// 网络依赖，裸 DownloadException 只说 URL，用户判断不出是哪个功能出了问题；取消照常传播。</summary>
    private async Task<KuroIndexFile> FetchPackIndexFileAsync(
        KuroResourcePack pack, string cdn, string tier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pack.IndexFile))
        {
            throw new UpdateException($"Kuro resource pack \"{tier}\" has no indexFile.");
        }

        var json = await FetchTextWithContextAsync(
            KuroUrlBuilder.BuildFileUrl(cdn, null, pack.IndexFile!), pack.IndexFileMd5,
            $"the \"{tier}\" resource pack index", cancellationToken).ConfigureAwait(false);
        return ParseJson<KuroIndexFile>(json, $"resource pack \"{tier}\" indexFile.json");
    }

    public async Task<ResourcePackManifest?> GetResourcePackManifestAsync(
        GameServer server, string tier, CancellationToken cancellationToken = default)
    {
        // 白名单外的档位/不支持档位的服务器：零网络返回 null（清理入口据此禁用）
        if (!ResourceTiers.IsValid(tier) || !SupportsResourcePackDownload(server))
        {
            return null;
        }

        var packIndex = await FetchResourcePackIndexAsync(server, cancellationToken).ConfigureAwait(false);
        if (packIndex.ResourcePacks?.GetValueOrDefault(tier) is not { } pack)
        {
            return null;
        }

        var packCdn = KuroCdnSelector.SelectCdn(packIndex.CdnList) ?? RequireCdn(RequireDefault(await FetchIndexAsync(server, cancellationToken).ConfigureAwait(false)));
        var packIndexFile = await FetchPackIndexFileAsync(pack, packCdn, tier, cancellationToken).ConfigureAwait(false);
        return new ResourcePackManifest(tier, PackDirectoryFor(tier), pack.Version ?? "", ToManifestFiles(packIndexFile.Resource, packCdn, pack.BaseUrl));
    }

    /// <summary>本次同步实际落盘的档位：支持档位索引的服务器取配置档（未设置 = hd）；
    /// 不支持的服务器恒记 hd —— 内容就是官方默认 bundle（common + HD），档位只作启动参数。</summary>
    private string SyncTier(GameServer server, string? resourceTier) =>
        SupportsResourcePackDownload(server) ? ResourceTiers.Effective(resourceTier) : ResourceTiers.Hd;

    /// <summary>档位资源目录（相对安装目录，官方实测 2026-10-16：Client/Content/{SD,HD,UHD}）。</summary>
    internal static string PackDirectoryFor(string tier) => $"Client/Content/{tier.ToUpperInvariant()}";

    /// <summary>dest 是否落在指定档位的资源目录之下（档位目录内的一切都算该档位的内容）。
    /// dest 为 null（STJ null-over-initializer 形态）按"不在"处理——单条畸形不得炸清单获取（F42 同族）。</summary>
    private static bool IsInTierDirectory(string? dest, string tier) =>
        dest is not null && dest.StartsWith(PackDirectoryFor(tier) + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>不在本次清单内、但必须保留的档位目录（未选中档位）：同步收尾清理不得隐式删除，
    /// 删除只经用户的显式「清理其他档位」动作（GameUpdateService 侧 ResourcePackService）。</summary>
    private static IReadOnlyList<string> PreservedTierDirs(string effectiveTier) =>
        [.. ResourceTiers.OtherTiers(effectiveTier).Select(PackDirectoryFor)];

    /// <summary>读取文本：失败折算带上下文（什么资源）的 UpdateException，取消照常传播。</summary>
    private async Task<string> FetchTextWithContextAsync(
        string url, string? expectedMd5, string what, CancellationToken cancellationToken)
    {
        try
        {
            return await FetchTextAsync(url, expectedMd5, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UpdateException($"Failed to fetch {what} ({url}): {ex.Message}", ex);
        }
    }

    private async Task<string> FetchTextAsync(string url, string? expectedMd5, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "yagl", $"kuro-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        try
        {
            await downloader.DownloadFileAsync(
                new DownloadRequest(url, tempPath, null, expectedMd5), null, cancellationToken).ConfigureAwait(false);
            return await File.ReadAllTextAsync(tempPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // 下载器的失败路径会有意保留 <目标>.temp 以便断点续传——该语义对每次换新
            // Guid 的临时 index.json 永不适用，这里一并清掉（2026-09-20 复审修复）
            FileUtilities.DeleteQuiet(tempPath);
            FileUtilities.DeleteQuiet(tempPath + ".temp");
        }
    }

    /// <summary>资源条目 → 清单文件；withUrl=false 用于差分组的源/目标描述（只需校验信息，URL 无意义）。
    /// dest 空白的条目跳过（F42）：显式 null 会覆盖 ="" 初始化器，直通 BuildFileUrl/下游 ResolveSafe
    /// 即 NRE——单条畸形炸全量更新、分类 Unknown，与 ParsePage 对缺失字段 continue 的语义对齐。</summary>
    private static IReadOnlyList<ManifestFile> ToManifestFiles(
        IEnumerable<KuroResourceEntry> entries, string cdn, string? folder, bool withUrl = true) =>
        [.. entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Dest))
            .Select(entry => new ManifestFile(
                entry.Dest,
                entry.Size,
                entry.Md5,
                withUrl ? KuroUrlBuilder.BuildFileUrl(cdn, entry.FromFolder ?? folder, entry.Dest) : null))];

    private static IReadOnlyList<PatchGroup> ToGroups(
        IEnumerable<KuroGroupInfo>? groups, string cdn,
        string? patchBaseUrl, string? defaultBaseUrl, string? resourcesBasePath,
        IReadOnlyList<KuroResourceEntry>? resourceEntries = null)
    {
        if (groups is null)
        {
            return [];
        }

        // 组层常无 size/md5（2026-10-02 真机实测组条目仅 dest/srcFiles/dstFiles 三键），而 resource[]
        // 的同名 krpdiff 条目携带两者——按名回填，使下载校验与进度总量可用（参考实现同语义：
        // krpdiff_info = resource_by_dest.get(...)，缺失再 HEAD 探测）。
        // 重复 dest 用 TryAdd 先登记者胜（不抛：清单不可信，F42 同族——单条畸形不得炸清单获取）
        var resourceByDest = resourceEntries is null
            ? null
            : BuildResourceByDest(resourceEntries);

        return [.. groups
            .Where(group => !string.IsNullOrWhiteSpace(group.Dest))
            .Select(group =>
            {
                var diffEntry = resourceByDest is not null && resourceByDest.TryGetValue(group.Dest, out var entry)
                    ? entry
                    : null;
                return new PatchGroup(
                    group.Dest,
                    diffEntry is { Size: > 0 } ? diffEntry.Size : group.Size,
                    diffEntry is { Md5: { Length: > 0 } md5 } ? md5 : group.Md5,
                    ToManifestFiles(group.SrcFiles, cdn, resourcesBasePath, withUrl: false),
                    ToManifestFiles(group.DstFiles, cdn, resourcesBasePath, withUrl: false),
                    KuroUrlBuilder.BuildPatchUrl(cdn, patchBaseUrl, defaultBaseUrl, group.Dest));
            })];
    }

    /// <summary>增量清单条目是否为 krpdiff/krdiff 差分文件（按扩展名判定，大小写不敏感）。
    /// 差分文件由 Groups 表达（差分目录前缀），不作为直下安装文件下载。</summary>
    private static bool IsDiffResource(string dest) =>
        dest.EndsWith(".krpdiff", StringComparison.OrdinalIgnoreCase)
        || dest.EndsWith(".krdiff", StringComparison.OrdinalIgnoreCase);

    /// <summary>resource 条目按 dest 建索引（回填 size/md5 用）；重复 dest 先登记者胜，
    /// 空白 dest 跳过——与 ToManifestFiles 的顺序处理/空白过滤语义一致。</summary>
    private static Dictionary<string, KuroResourceEntry> BuildResourceByDest(
        IReadOnlyList<KuroResourceEntry> entries)
    {
        var map = new Dictionary<string, KuroResourceEntry>(entries.Count, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Dest))
            {
                map.TryAdd(entry.Dest, entry);
            }
        }

        return map;
    }

    /// <summary>取直下资源列表中第一个非空 fromFolder 作为无 fromFolder 条目的回退目录。
    /// 参考实现（ww-manager incremental.py）语义 + 2026-09-29 真机实证：官方增量清单常带少量
    /// fromFolder 条目指向目标版本 zip/ 目录、其余直下条目共用（krpdiff 已先行滤除，走差分目录）；
    /// 预载窗口期 patchEntry.baseUrl 是差分包目录（真机 .../3.6.1/resources/ 全 404），
    /// 不得优先于它作资源前缀。</summary>
    private static string? FirstFromFolder(IEnumerable<KuroResourceEntry> entries) =>
        entries.Select(entry => entry.FromFolder).FirstOrDefault(folder => !string.IsNullOrWhiteSpace(folder));

    private static IReadOnlyList<string> GetPatchSourceVersions(KuroResourceConfig? config) =>
        config?.PatchConfig is null
            ? []
            : [.. config.PatchConfig
                .Select(p => p.Version)
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!)];

    private static KuroResourceBlock RequireDefault(KuroLauncherIndex index) =>
        index.Default ?? throw new UpdateException("Kuro index.json has no default resource block.");

    /// <summary>取服务器当前版本；缺失即拒收——空版本一旦登记落盘，IsNewer 恒判"无更新"，
    /// 该游戏此后永久失去更新检测且无自愈路径（2026-09-20 复审修复）。</summary>
    private static string RequireVersion(KuroResourceBlock block)
    {
        var version = block.Config?.Version ?? block.Version;
        return string.IsNullOrEmpty(version)
            ? throw new UpdateException("Kuro index.json has no version.")
            : version;
    }

    /// <summary>取 CDN 基址：优先本块 cdnList；本块无可用节点且 fallback 非空时回退 fallback 块，仍无才拒收。</summary>
    private static string RequireCdn(KuroResourceBlock block, KuroResourceBlock? fallback = null) =>
        KuroCdnSelector.SelectCdn(block.CdnList)
        ?? (fallback is null ? null : KuroCdnSelector.SelectCdn(fallback.CdnList))
        ?? throw new UpdateException("Kuro index.json cdnList has no usable node (K1/K2).");

    private static KuroResourceConfig RequireConfig(KuroResourceBlock block) =>
        block.Config ?? throw new UpdateException("Kuro index.json has no config node.");

    private static string RequireIndexFile(KuroResourceConfig config) =>
        config.IndexFile
        ?? throw new UpdateException("Kuro index.json config has no indexFile.");

    /// <summary>取目标档位包配置：官方档位索引缺该档位即拒收——绝不能返回"缺目标档位包"的
    /// 清单（同步收尾会把目标档位目录当游离文件删掉，几十 GB，2026-10-19）。</summary>
    private static KuroResourcePack RequireResourcePack(KuroLauncherIndex index, string tier) =>
        index.ResourcePacks?.GetValueOrDefault(tier)
        ?? throw new UpdateException(
            $"Kuro resource pack index has no \"{tier}\" pack; cannot sync the selected resource tier.");

    private static T ParseJson<T>(string json, string source) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json)
                   ?? throw new UpdateException($"Kuro response ({source}) is empty.");
        }
        catch (JsonException ex)
        {
            throw new UpdateException($"Failed to parse Kuro response ({source}): {ex.Message}", ex);
        }
    }
}
