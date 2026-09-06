using System.Net.Http.Json;
using System.Text.Json;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Channels.Hypergryph.Models;
using Microsoft.Extensions.Logging;

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
/// </summary>
public sealed class GryphlineChannelApi(HttpClient httpClient, ILogger? logger = null) : IGameChannelApi
{
    public const string ApiBaseOptionKey = "apiBase";
    public const string AppcodeOptionKey = "appcode";
    public const string ChannelOptionKey = "channel";
    public const string SubChannelOptionKey = "subChannel";

    // 国际服（osWinRel）实测参数，作为 options 未提供时的缺省值
    private const string GameAppcode = "YDUTE5gscDZ229CW";
    private const string ChannelId = "6";
    private const string SubChannelId = "9999";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient = httpClient;

    public async Task<ChannelVersionInfo> GetVersionInfoAsync(GameServer server, CancellationToken cancellationToken = default)
    {
        var response = await PostGetLatestGameAsync(server, clientVersion: "", cancellationToken);

        string? predownloadVersion = null;
        if (response.Patch is { } patch
            && patch.ValueKind == JsonValueKind.Object
            && patch.TryGetProperty("version", out var versionElement)
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

    public async Task<GameManifest> GetManifestAsync(GameServer server, string version, CancellationToken cancellationToken = default)
    {
        var response = await PostGetLatestGameAsync(server, clientVersion: "", cancellationToken);
        return ToPackageManifest(response.Version, response.Pkg!);
    }

    public Task<GameManifest?> GetIncrementalManifestAsync(
        GameServer server, string fromVersion, string toVersion, CancellationToken cancellationToken = default)
        => Task.FromResult<GameManifest?>(null); // 包式渠道：更新走整包，无按版本差分清单

    public async Task<GameManifest?> GetPredownloadManifestAsync(GameServer server, CancellationToken cancellationToken = default)
    {
        var response = await PostGetLatestGameAsync(server, clientVersion: "", cancellationToken);
        if (response.Patch is not { } patch || patch.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var patchVersion = patch.TryGetProperty("version", out var v) ? v.GetString() : null;
        if (patchVersion is null || !patch.TryGetProperty("pkg", out var pkgElement))
        {
            return null;
        }

        var pkg = pkgElement.Deserialize<PackageInfo>(JsonOptions);
        return pkg is null || pkg.Packs.Count == 0 ? null : ToPackageManifest(patchVersion, pkg);
    }

    private async Task<GameVersionResponse> PostGetLatestGameAsync(
        GameServer server, string clientVersion, CancellationToken cancellationToken)
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
                        Version = clientVersion,
                        Appcode = OptionOrDefault(server, AppcodeOptionKey, GameAppcode),
                        Channel = OptionOrDefault(server, ChannelOptionKey, ChannelId),
                        SubChannel = OptionOrDefault(server, SubChannelOptionKey, SubChannelId),
                        DeviceId = "yetanothervariant-game-launcher",
                    },
                },
            ],
        };

        logger?.LogDebug("POST {Url}（clientVersion={Version}）", $"{apiBase}/proxy/batch_proxy", clientVersion);

        using var response = await _httpClient.PostAsJsonAsync(
            $"{apiBase}/proxy/batch_proxy", payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        GameVersionResponse gameResponse;
        try
        {
            var batch = await response.Content.ReadFromJsonAsync<BatchProxyResponse>(cancellationToken);
            var first = batch?.ProxyRsps is { Count: > 0 } rsps
                ? rsps[0]
                : throw new UpdateException("GRYPHLINE batch_proxy response is empty.");

            gameResponse = first.GetProperty("get_latest_game_rsp").Deserialize<GameVersionResponse>(JsonOptions)
                           ?? throw new UpdateException("GRYPHLINE get_latest_game_rsp is empty.");
        }
        catch (JsonException ex)
        {
            throw new UpdateException($"Failed to parse GRYPHLINE response: {ex.Message}", ex);
        }

        if (gameResponse.Pkg is null)
        {
            throw new UpdateException("GRYPHLINE response is missing pkg (package info).");
        }

        return gameResponse;
    }

    private static string OptionOrDefault(GameServer server, string key, string fallback)
        => server.Options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;

    private static GameManifest ToPackageManifest(string version, PackageInfo pkg)
    {
        var files = new List<ManifestFile>(pkg.Packs.Count);
        for (var i = 0; i < pkg.Packs.Count; i++)
        {
            var pack = pkg.Packs[i];
            files.Add(new ManifestFile(
                Path: GetFileNameFromUrl(pack.Url) ?? $"package-{i}.bin",
                Size: long.TryParse(pack.PackageSize, out var size) ? size : 0,
                Md5: pack.Md5,
                Url: pack.Url));
        }

        return new GameManifest { Version = version, EntriesAreArchives = true, Files = files };
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
