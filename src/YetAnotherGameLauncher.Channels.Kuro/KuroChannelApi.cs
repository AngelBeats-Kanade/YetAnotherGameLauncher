using System.Globalization;
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
        var baseEntries = indexFile.Resource ?? [];
        if (tier == ResourceTiers.Hd)
        {
            var hdFiles = ToManifestFiles(baseEntries, cdn, config.BaseUrl ?? block.ResourcesBasePath);
            // 空清单安全闸（2026-10-26 复审 P1）：indexFile.json 是 {} / {"resource":[]} / 全空白
            // dest 时，返回空清单会让同步收尾把已装内容全当游离文件删掉（几十 GB）。与包清单空
            // 同一哲学：宁可拒收让用户重试。
            EnsureNonEmptyFullManifest("indexFile.json", hdFiles.Count);
            var hdManifest = new GameManifest
            {
                Version = version,
                Files = hdFiles,
                Groups = ToGroups(indexFile.GroupInfos, cdn, patchBaseUrl: null, config.BaseUrl, block.ResourcesBasePath),
                ResourceTier = ResourceTiers.Hd,
                PreservedPaths = PreservedTierDirs(ResourceTiers.Hd),
            };
            ManifestBoundary.ValidateManifest(hdManifest, "Kuro full manifest");
            return hdManifest;
        }

        // 非 HD：基础清单里的**所有**档位目录都裁掉，再由目标档位包单独提供目标档内容。
        // 只裁"其它档"会把 base 里的目标档旧条目与包条目叠在一起（同路径/旧内容），
        // 也让"目标档内容只来自档位包"这一来源事实失效（2026-10-23 清单边界批）。
        // base 裁完为空同样视为异常：只剩档位包的清单会让收尾清理删掉全部 common 内容。
        var baseFiles = ToManifestFiles(
            baseEntries.Where(e => !ResourceTiers.All.Any(t => IsInTierDirectory(e.Dest, t))),
            cdn, config.BaseUrl ?? block.ResourcesBasePath);
        EnsureNonEmptyFullManifest($"base manifest common files (tier {tier})", baseFiles.Count);

        var packIndex = await FetchResourcePackIndexAsync(server, cancellationToken).ConfigureAwait(false);
        var pack = RequireResourcePack(packIndex, tier);
        // 版本一致性（2026-10-23）：档位包与 base 清单必须同版。窗口期官方可能先切 base、
        // 后切档位包；混用两版内容会让修复轮/游戏挂载对不上，硬失败让用户稍后重试或暂切 HD。
        EnsurePackVersion(pack, tier, RequireVersion(block));
        // 档位索引的节点在顶层下发（包配置块无 cdnList，2026-10-16 实测）；顶层无可用节点回退标准块
        var packCdn = KuroCdnSelector.SelectCdn(packIndex.CdnList) ?? cdn;
        var packIndexFile = await FetchPackIndexFileAsync(pack, packCdn, tier, cancellationToken).ConfigureAwait(false);
        var packFiles = ToManifestFiles(packIndexFile.Resource ?? [], packCdn, pack.BaseUrl);

        // 包清单空 = 拉到了残缺/异常响应的信号：绝不能返回"缺目标档位包"的清单——同步收尾会把
        // 目标档位目录当游离文件删掉（几十 GB）。宁可拒收让用户重试（与 dstFiles 缺 url 的
        // "拒绝静默降级"同哲学：官方在档位包上永远给 100 个条目）
        if (packFiles.Count == 0)
        {
            throw new UpdateException(
                $"Kuro resource pack \"{tier}\" lists no files; refusing to sync without it " +
                "(a pack-less manifest would delete the tier's local files as stale). Please retry.");
        }

        // 体积诊断（2026-10-26 复审 P2）：resourcePacks[tier].size 自述"未压缩总字节数"，但真机语义
        // 尚未核对，故只告警不设门禁——把它当硬失败条件，一旦口径不符（含额外元数据/压缩前后混用）
        // 会把 sd/uhd 同步永久挡死且无回退。真机核对后再考虑升级为守卫。
        var packBytes = packFiles.Sum(f => f.Size);
        if (pack.Size > 0 && packBytes != pack.Size)
        {
            logger?.LogWarning(
                "Kuro resource pack {Tier} declares {Declared} bytes but lists {Actual}; " +
                "treating the per-file manifest as authoritative (size is diagnostic only).",
                tier, pack.Size, packBytes);
        }

        ManifestBoundary.ValidatePackFiles(
            PackDirectoryFor(tier), packFiles, $"Kuro \"{tier}\" resource pack");

        // 目标档条目若在 base 清单里出现、却不在档位包里，裁掉后它既不在清单、也不在保留名单
        //（PreservedPaths 只保其它档），同步收尾清理会把它当游离文件删除且无自愈路径——
        // 与"缺目标档包拒收"同一哲学，显式拒收而不是静默丢条目（2026-10-23 复审）。
        // 注：实测 base 形态是 common + HD，目标档条目通常不存在，此守卫在真实数据上多为空操作；
        // 保留它是防官方形态变化时静默丢内容。
        var packPaths = packFiles
            .Select(f => f.Path.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var droppedTargetTier = baseEntries
            .Where(e => !string.IsNullOrWhiteSpace(e.Dest)
                && IsInTierDirectory(e.Dest, tier)
                && !packPaths.Contains(e.Dest.Replace('\\', '/')))
            .ToList();
        if (droppedTargetTier.Count > 0)
        {
            throw new UpdateException(
                $"Kuro base manifest lists {droppedTargetTier.Count} \"{tier}\" tier file(s) the " +
                $"\"{tier}\" resource pack does not contain (e.g. {droppedTargetTier[0].Dest}); refusing to sync - " +
                "dropping them would let stale cleanup delete tier content.");
        }

        // base 已裁掉所有档位目录、档位包条目被限制在目标档目录下，两者天然不相交——跨列表不会
        // 出现同目标双条目（同目标异构写法由 IsInTierDirectory 的 '\'→'/' 归一键覆盖）。
        var manifest = new GameManifest
        {
            Version = version,
            Files = [.. baseFiles, .. packFiles],
            Groups = ToGroups(
                indexFile.GroupInfos?.Where(g => !ResourceTiers.All.Any(t => IsInTierDirectory(g.Dest, t))),
                cdn, patchBaseUrl: null, config.BaseUrl, block.ResourcesBasePath),
            ResourceTier = tier,
            PreservedPaths = PreservedTierDirs(tier),
        };
        ManifestBoundary.ValidateManifest(manifest, "Kuro full manifest");
        return manifest;
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

            // 版本一致性守卫只放在 GetManifestAsync（档位包条目真正并入清单的地方），本方法不设：
            // 这里产出的差量清单不含任何档位包内容，档位包落盘由更新/应用后的全量修复轮承担，
            // 那里 EnsurePackVersion 已经把关。此前在此按 toVersion 硬校验是过度拦截——预下载
            // 窗口期内 toVersion（预下载目标版）按定义不是线上版，而 GetManifestAsync 又按线上版
            // 校验，resourcePacks[tier].version 是单值，两种口径必有一侧不成立：sd/uhd 的预下载
            // 会被无解地挡死（2026-10-23 复审阻塞项）。
            var tier = SyncTier(server, resourceTier);

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
            // 档位（2026-10-19）：sd/uhd 时裁掉**所有**档位目录的条目与差分组——档位包自身
            // 跨版本不变（官方实测 hd/sd/uhd 的 3.7.0 → 3.7.1 增量清单为空：resource 与
            // deleteFiles 均空），其落盘由更新/应用后的全量修复轮按档位清单补齐
            // （GameUpdateService.RepairAgainstManifestAsync）。HD 仍保留 HD 条目（它就是基础包）。
            var tiersToCrop = tier == ResourceTiers.Hd ? ResourceTiers.OtherTiers(tier) : ResourceTiers.All;
            var directResources = (patchIndexFile.Resource ?? [])
                .Where(r => !IsDiffResource(r.Dest) && !tiersToCrop.Any(t => IsInTierDirectory(r.Dest, t)))
                .ToList();

            // 废弃清单同样裁掉档位目录（2026-10-19 review 实锤：原样透传会让 SD/UHD 更新
            // 删掉 HD 目录里清单点名的文件，与"切档不删旧档、删除只经显式清理"冲突）；
            // 另跳过 .yagl/Saved/官方兼容目录——删除清单是外部输入，不能碰用户数据/启动器状态。
            var deleteFiles = new List<string>();
            foreach (var path in patchIndexFile.DeleteFiles ?? [])
            {
                // F42 同族：空白条目（含 STJ 对 `"deleteFiles": [null]` 解析出的 null）不指向任何
                // 可删文件，跳过即可——不能让它 NRE 掉整份清单获取（2026-10-26 复审）。
                if (string.IsNullOrWhiteSpace(path))
                {
                    logger?.LogWarning("Kuro incremental deleteFiles has a blank entry; skipping it.");
                    continue;
                }

                if (tiersToCrop.Any(t => IsInTierDirectory(path, t)))
                {
                    continue;
                }

                if (ManifestBoundary.IsReservedPath(path))
                {
                    logger?.LogWarning(
                        "Kuro incremental deleteFiles lists reserved path {Path}; sparing it.", path);
                    continue;
                }

                deleteFiles.Add(path);
            }

            var manifest = new GameManifest
            {
                Version = toVersion,
                Files = ToManifestFiles(
                    directResources, cdn,
                    folder: FirstFromFolder(directResources) ?? patchEntry.BaseUrl ?? config.BaseUrl ?? block.ResourcesBasePath),
                Groups = ToGroups(
                    patchIndexFile.GroupInfos?.Where(g => !tiersToCrop.Any(t => IsInTierDirectory(g.Dest, t))),
                    cdn, patchEntry.BaseUrl, config.BaseUrl, block.ResourcesBasePath,
                    patchIndexFile.Resource),
                DeleteFiles = deleteFiles,
                ResourceTier = tier,
                PreservedPaths = PreservedTierDirs(tier),
            };
            ManifestBoundary.ValidateManifest(manifest, "Kuro incremental manifest");
            return manifest;
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
        var files = ToManifestFiles(packIndexFile.Resource ?? [], packCdn, pack.BaseUrl);
        // 清理/测量同样只信"条目在目标档目录下"：混入别的档位会被算成本档体积，甚至被清理误删。
        ManifestBoundary.ValidatePackFiles(
            PackDirectoryFor(tier), files, $"Kuro \"{tier}\" resource pack manifest");
        return new ResourcePackManifest(tier, PackDirectoryFor(tier), pack.Version ?? "", files);
    }

    /// <summary>本次同步实际落盘的档位：支持档位索引的服务器取配置档（未设置 = hd）；
    /// 不支持的服务器恒记 hd —— 内容就是官方默认 bundle（common + HD），档位只作启动参数。</summary>
    private string SyncTier(GameServer server, string? resourceTier) =>
        SupportsResourcePackDownload(server) ? ResourceTiers.Effective(resourceTier) : ResourceTiers.Hd;

    /// <summary>档位资源目录（相对安装目录，官方实测 2026-10-16：Client/Content/{SD,HD,UHD}）。</summary>
    internal static string PackDirectoryFor(string tier) => $"Client/Content/{tier.ToUpperInvariant()}";

    /// <summary>dest 是否落在指定档位的资源目录之下（档位目录内的一切都算该档位的内容）。
    /// dest 为 null（STJ null-over-initializer 形态）按"不在"处理——单条畸形不得炸清单获取（F42 同族）。
    /// 判定前把 '\' 归一成 '/'：JSON 里允许反斜杠分隔，`Client\Content\SD\x` 与 `Client/Content/SD/x`
    /// 是同一目标，不归一会漏裁、与档位包条目叠成"双条目写同一文件"（2026-10-26 复审）。</summary>
    private static bool IsInTierDirectory(string? dest, string tier) =>
        dest is not null
        && dest.Replace('\\', '/').StartsWith(PackDirectoryFor(tier) + "/", StringComparison.OrdinalIgnoreCase);

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
    /// 即 NRE——单条畸形炸全量更新、分类 Unknown，与 ParsePage 对缺失字段 continue 的语义对齐。
    /// 重复 dest 先登记者胜（与 <see cref="BuildResourceByDest"/> 同一决定：清单不可信，F42 同族——
    /// 冗余条目不得炸清单获取），去重同时消除"两个条目并发写同一目标"的竞态（2026-10-26 复审）。
    /// 去重键按 '\'→'/' 归一后 Ordinal：只有**完全同名**的 dest 被去重（Windows 下它们必是同一文件）；
    /// Linux 下大小写仅异是不同文件、不去重（少收一条会让清理把它当游离文件删掉）。</summary>
    private static IReadOnlyList<ManifestFile> ToManifestFiles(
        IEnumerable<KuroResourceEntry> entries, string cdn, string? folder, bool withUrl = true)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Dest))
            .Where(entry => seen.Add(entry.Dest.Replace('\\', '/')))
            .Select(entry => new ManifestFile(
                entry.Dest,
                entry.Size,
                entry.Md5,
                withUrl ? KuroUrlBuilder.BuildFileUrl(cdn, entry.FromFolder ?? folder, entry.Dest) : null))];
    }

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
                    ToManifestFiles(group.SrcFiles ?? [], cdn, resourcesBasePath, withUrl: false),
                    ToManifestFiles(group.DstFiles ?? [], cdn, resourcesBasePath, withUrl: false),
                    KuroUrlBuilder.BuildPatchUrl(cdn, patchBaseUrl, defaultBaseUrl, group.Dest));
            })];
    }

    /// <summary>增量清单条目是否为 krpdiff/krdiff 差分文件（按扩展名判定，大小写不敏感）。
    /// 差分文件由 Groups 表达（差分目录前缀），不作为直下安装文件下载。
    /// dest 可空：STJ 的 null-over-initializer 会让非空注解属性变成 null，而本方法在
    /// `ToManifestFiles` 滤空白**之前**调用——不判空就是 NRE 而非 UpdateException
    ///（增量链没有整包回退，重试会一直撞同一处；2026-10-26 复审）。</summary>
    private static bool IsDiffResource(string? dest) =>
        !string.IsNullOrWhiteSpace(dest)
        && (dest.EndsWith(".krpdiff", StringComparison.OrdinalIgnoreCase)
            || dest.EndsWith(".krdiff", StringComparison.OrdinalIgnoreCase));

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

    /// <summary>全量清单必须有可安装文件：空清单会让同步收尾把已装内容全当游离文件删除（几十 GB）。
    /// 与"档位包包清单空拒收"同一哲学（2026-10-26 复审 P1）。注意只把关**全量清单构造**——
    /// 增量清单可以为空 Files + 非空 Groups/DeleteFiles，不能套用此规则。</summary>
    private static void EnsureNonEmptyFullManifest(string what, int fileCount)
    {
        if (fileCount == 0)
        {
            throw new UpdateException(
                $"Kuro {what} lists no files; refusing to sync " +
                "(an empty full manifest would delete installed content as stale). Please retry.");
        }
    }

    /// <summary>版本一致性守卫：档位包版本必须与本次 base 内容同版。缺失版本同样拒收——
    /// 无法确认同版时宁可让用户稍后重试，也不返回来源不明的档位内容（2026-10-23）。</summary>
    private static void EnsurePackVersion(KuroResourcePack pack, string tier, string baseVersion)
    {
        if (string.IsNullOrWhiteSpace(pack.Version))
        {
            throw new UpdateException(
                $"Kuro resource pack \"{tier}\" has no version; cannot verify it matches game content " +
                $"{baseVersion}. Please retry later or switch to HD temporarily.");
        }

        if (!VersionsMatch(pack.Version, baseVersion))
        {
            throw new UpdateException(
                $"Kuro resource pack \"{tier}\" is version {pack.Version}, but the game content is " +
                $"{baseVersion}; refusing to sync mismatched tier content. " +
                "Please retry later or switch to HD temporarily.");
        }
    }

    /// <summary>版本等价比较：按点分段数字比较并补零，3.7 ≡ 3.7.0 ≡ 3.7.0.0；
    /// 非纯数字段（如带预发布后缀）回退逐字符比较，避免把不同构建误判为同版。</summary>
    internal static bool VersionsMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var leftParts = ParseVersionParts(left);
        var rightParts = ParseVersionParts(right);
        if (leftParts is null || rightParts is null)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        var length = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < length; i++)
        {
            var leftValue = i < leftParts.Length ? leftParts[i] : 0;
            var rightValue = i < rightParts.Length ? rightParts[i] : 0;
            if (leftValue != rightValue)
            {
                return false;
            }
        }

        return true;
    }

    private static int[]? ParseVersionParts(string value)
    {
        var parts = value.Split('.');
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

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
