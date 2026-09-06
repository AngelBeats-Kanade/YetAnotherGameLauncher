using System.Text.Json;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Channels.Kuro.Models;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>
/// 库洛游戏渠道（鸣潮官服/B 服/国际服）。
/// indexUrl 由 games.json 的 server.options 提供，代码不内置任何服务器地址。
/// </summary>
public sealed class KuroChannelApi(IDownloader downloader, ILogger? logger = null) : IGameChannelApi
{
    public const string IndexUrlOptionKey = "indexUrl";

    private readonly IDownloader _downloader = downloader;

    public async Task<ChannelVersionInfo> GetVersionInfoAsync(GameServer server, CancellationToken cancellationToken = default)
    {
        var index = await FetchIndexAsync(server, cancellationToken);
        var block = RequireDefault(index);

        var predownloadConfig = index.Predownload?.Config;
        return new ChannelVersionInfo
        {
            LatestVersion = block.Config?.Version ?? block.Version ?? "",
            PatchSourceVersions = GetPatchSourceVersions(block.Config),
            PredownloadAvailable = predownloadConfig is not null,
            PredownloadVersion = predownloadConfig?.Version,
            PredownloadPatchSourceVersions = GetPatchSourceVersions(predownloadConfig),
        };
    }

    public async Task<GameManifest> GetManifestAsync(GameServer server, string version, CancellationToken cancellationToken = default)
    {
        var index = await FetchIndexAsync(server, cancellationToken);
        var block = RequireDefault(index);
        var cdn = RequireCdn(block);
        var config = RequireConfig(block);

        var indexFileJson = await FetchTextAsync(
            KuroUrlBuilder.BuildFileUrl(cdn, null, RequireIndexFile(config)),
            config.IndexFileMd5,
            cancellationToken);
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
        var index = await FetchIndexAsync(server, cancellationToken);
        var block = RequireDefault(index);
        var cdn = RequireCdn(block);
        var config = RequireConfig(block);

        var patchEntry = config.PatchConfig?.FirstOrDefault(p => p.Version == fromVersion);
        if (patchEntry?.IndexFile is null)
        {
            return null;
        }

        var patchIndexJson = await FetchTextAsync(
            KuroUrlBuilder.BuildFileUrl(cdn, null, patchEntry.IndexFile),
            patchEntry.IndexFileMd5,
            cancellationToken);
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

    private async Task<KuroLauncherIndex> FetchIndexAsync(GameServer server, CancellationToken cancellationToken)
    {
        if (!server.Options.TryGetValue(IndexUrlOptionKey, out var indexUrl) || string.IsNullOrWhiteSpace(indexUrl))
        {
            throw new UpdateException($"Server \"{server.Name}\" is missing the {IndexUrlOptionKey} option.");
        }

        logger?.LogDebug("Fetching Kuro index.json: {Url}", indexUrl);
        var json = await FetchTextAsync(indexUrl, expectedMd5: null, cancellationToken);
        return ParseJson<KuroLauncherIndex>(json, "index.json");
    }

    private async Task<string> FetchTextAsync(string url, string? expectedMd5, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "yagl", $"kuro-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        try
        {
            await _downloader.DownloadFileAsync(
                new DownloadRequest(url, tempPath, null, expectedMd5), null, cancellationToken);
            return await File.ReadAllTextAsync(tempPath, cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private static IReadOnlyList<ManifestFile> ToManifestFiles(
        IEnumerable<KuroResourceEntry> entries, string cdn, string? folder) =>
        [.. entries.Select(entry => new ManifestFile(
            entry.Dest,
            entry.Size,
            entry.Md5,
            entry.ChunkInfos is null ? null : [.. entry.ChunkInfos.Select(c => new ManifestChunk(c.Start, c.End, c.Md5))],
            KuroUrlBuilder.BuildFileUrl(cdn, entry.FromFolder ?? folder, entry.Dest)))];

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
            ToManifestFiles(group.SrcFiles, cdn, resourcesBasePath)
                .Select(f => f with { Url = null })
                .ToList(),
            ToManifestFiles(group.DstFiles, cdn, resourcesBasePath)
                .Select(f => f with { Url = null })
                .ToList(),
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
