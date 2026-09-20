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

    public async Task<GameManifest> GetManifestAsync(GameServer server, string version, CancellationToken cancellationToken = default)
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

        return new GameManifest
        {
            Version = version,
            Files = ToManifestFiles(indexFile.Resource, cdn, config.BaseUrl ?? block.ResourcesBasePath),
            Groups = ToGroups(indexFile.GroupInfos, cdn, patchBaseUrl: null, config.BaseUrl, block.ResourcesBasePath),
        };
    }

    public async Task<GameManifest?> GetIncrementalManifestAsync(
        GameServer server, string fromVersion, string toVersion, CancellationToken cancellationToken = default)
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

            var cdn = RequireCdn(block);

            var patchIndexJson = await FetchTextAsync(
                KuroUrlBuilder.BuildFileUrl(cdn, null, patchEntry.IndexFile),
                patchEntry.IndexFileMd5,
                cancellationToken).ConfigureAwait(false);
            var patchIndexFile = ParseJson<KuroIndexFile>(patchIndexJson, "incremental indexFile.json");

            return new GameManifest
            {
                Version = toVersion,
                Files = ToManifestFiles(
                    patchIndexFile.Resource, cdn,
                    folder: patchEntry.BaseUrl ?? config.BaseUrl ?? block.ResourcesBasePath),
                Groups = ToGroups(patchIndexFile.GroupInfos, cdn, patchEntry.BaseUrl, config.BaseUrl, block.ResourcesBasePath),
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

    /// <summary>资源条目 → 清单文件；withUrl=false 用于差分组的源/目标描述（只需校验信息，URL 无意义）。</summary>
    private static IReadOnlyList<ManifestFile> ToManifestFiles(
        IEnumerable<KuroResourceEntry> entries, string cdn, string? folder, bool withUrl = true) =>
        [.. entries.Select(entry => new ManifestFile(
            entry.Dest,
            entry.Size,
            entry.Md5,
            withUrl ? KuroUrlBuilder.BuildFileUrl(cdn, entry.FromFolder ?? folder, entry.Dest) : null))];

    private static IReadOnlyList<PatchGroup> ToGroups(
        IEnumerable<KuroGroupInfo>? groups, string cdn,
        string? patchBaseUrl, string? defaultBaseUrl, string? resourcesBasePath)
    {
        if (groups is null)
        {
            return [];
        }

        return [.. groups.Select(group => new PatchGroup(
            group.Dest,
            group.Size,
            group.Md5,
            ToManifestFiles(group.SrcFiles, cdn, resourcesBasePath, withUrl: false),
            ToManifestFiles(group.DstFiles, cdn, resourcesBasePath, withUrl: false),
            KuroUrlBuilder.BuildPatchUrl(cdn, patchBaseUrl, defaultBaseUrl, group.Dest)))];
    }

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

    private static string RequireCdn(KuroResourceBlock block) =>
        KuroCdnSelector.SelectCdn(block.CdnList)
        ?? throw new UpdateException("Kuro index.json cdnList has no usable node (K1/K2).");

    private static KuroResourceConfig RequireConfig(KuroResourceBlock block) =>
        block.Config ?? throw new UpdateException("Kuro index.json has no config node.");

    private static string RequireIndexFile(KuroResourceConfig config) =>
        config.IndexFile
        ?? throw new UpdateException("Kuro index.json config has no indexFile.");

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
