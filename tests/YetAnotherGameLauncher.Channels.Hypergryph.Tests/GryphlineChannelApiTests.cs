using System.Net;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Channels.Hypergryph;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Channels.Hypergryph.Tests;

public class GryphlineChannelApiTests
{
    private const string BatchProxyUrl = "https://launcher.gryphline.com/api/proxy/batch_proxy";

    private readonly StubHttpHandler _handler = new();

    private GryphlineChannelApi CreateApi() => new(new HttpClient(_handler));

    private static GameServer Server() => new()
    {
        Id = "global",
        Name = "国际服",
        Options = new Dictionary<string, string> { ["apiBase"] = "https://launcher.gryphline.com/api" },
    };

    private void RegisterBatchResponse(string getLatestGameRspJson)
    {
        _handler.Map(BatchProxyUrl, $$"""
            {
              "proxy_rsps": [
                {
                  "kind": "get_latest_game",
                  "get_latest_game_rsp": {{getLatestGameRspJson}}
                }
              ]
            }
            """);
    }

    [Fact]
    public async Task GetVersionInfo_ParsesLatestVersion()
    {
        RegisterBatchResponse("""
            { "version": "1.2.0", "action": 1, "pkg": { "packs": [ { "url": "https://cdn.example.com/pkg0.zip", "md5": "aa", "package_size": "100" } ], "total_size": "100" } }
            """);

        var info = await CreateApi().GetVersionInfoAsync(Server());

        Assert.Equal("1.2.0", info.LatestVersion);
        Assert.False(info.PredownloadAvailable);
        Assert.Empty(info.PatchSourceVersions);
    }

    [Fact]
    public async Task GetVersionInfo_PredownloadAvailable_WhenPatchHasVersion()
    {
        RegisterBatchResponse("""
            {
              "version": "1.2.0", "action": 1,
              "pkg": { "packs": [ { "url": "https://cdn.example.com/pkg0.zip", "md5": "aa", "package_size": "100" } ] },
              "patch": { "version": "1.3.0", "pkg": { "packs": [ { "url": "https://cdn.example.com/patch0.zip", "md5": "bb", "package_size": "50" } ] } }
            }
            """);

        var info = await CreateApi().GetVersionInfoAsync(Server());

        Assert.True(info.PredownloadAvailable);
        Assert.Equal("1.3.0", info.PredownloadVersion);
    }

    [Fact]
    public async Task GetManifest_BuildsArchiveManifestFromPacks()
    {
        RegisterBatchResponse("""
            {
              "version": "1.2.0", "action": 1,
              "pkg": {
                "packs": [
                  { "url": "https://cdn.example.com/game-pkg-0.zip", "md5": "aaaa", "package_size": "1024" },
                  { "url": "https://cdn.example.com/game-pkg-1.zip", "md5": "bbbb", "package_size": "2048" }
                ],
                "total_size": "3072"
              }
            }
            """);

        var manifest = await CreateApi().GetManifestAsync(Server(), "1.2.0");

        Assert.True(manifest.EntriesAreArchives);
        Assert.Equal("1.2.0", manifest.Version);
        Assert.Equal(2, manifest.Files.Count);

        var first = manifest.Files[0];
        Assert.Equal("game-pkg-0.zip", first.Path);
        Assert.Equal(1024, first.Size);
        Assert.Equal("aaaa", first.Md5);
        Assert.Equal("https://cdn.example.com/game-pkg-0.zip", first.Url);
    }

    [Fact]
    public async Task GetPredownloadManifest_ReturnsPatchPackage()
    {
        RegisterBatchResponse("""
            {
              "version": "1.2.0", "action": 1,
              "pkg": { "packs": [ { "url": "https://cdn.example.com/pkg0.zip", "md5": "aa", "package_size": "100" } ] },
              "patch": { "version": "1.3.0", "pkg": { "packs": [ { "url": "https://cdn.example.com/patch-1.3.0.zip", "md5": "bb", "package_size": "50" } ] } }
            }
            """);

        var manifest = await CreateApi().GetPredownloadManifestAsync(Server());

        Assert.NotNull(manifest);
        Assert.True(manifest.EntriesAreArchives);
        Assert.Equal("1.3.0", manifest.Version);
        Assert.Equal("patch-1.3.0.zip", Assert.Single(manifest.Files).Path);
    }

    [Fact]
    public async Task GetPredownloadManifest_NoPatch_ReturnsNull()
    {
        RegisterBatchResponse("""
            { "version": "1.2.0", "action": 1, "pkg": { "packs": [ { "url": "https://cdn.example.com/pkg0.zip", "md5": "aa", "package_size": "100" } ] } }
            """);

        var manifest = await CreateApi().GetPredownloadManifestAsync(Server());

        Assert.Null(manifest);
    }

    [Fact]
    public async Task MissingApiBase_Throws()
    {
        var server = new GameServer { Id = "x", Name = "X" };

        await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetVersionInfoAsync(server));
    }

    [Fact]
    public async Task EmptyProxyRsps_ThrowsUpdateException()
    {
        _handler.Map(BatchProxyUrl, "{ \"proxy_rsps\": [] }");

        await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetVersionInfoAsync(Server()));
    }

    [Fact]
    public async Task RequestBody_ContainsProtocolConstants()
    {
        HttpRequestMessage? captured = null;
        var handler = new CapturingHandler(request => captured = request)
        {
            Inner =
            {
                [BatchProxyUrl] = """
                    { "proxy_rsps": [ { "get_latest_game_rsp": { "version": "1.0.0", "pkg": { "packs": [ { "url": "https://cdn/x.zip", "md5": "a", "package_size": "1" } ] } } } ] }
                    """,
            },
        };
        var api = new GryphlineChannelApi(new HttpClient(handler));

        await api.GetVersionInfoAsync(Server());

        var body = await captured!.Content!.ReadAsStringAsync();
        Assert.Contains("\"get_latest_game\"", body);
        Assert.Contains("YDUTE5gscDZ229CW", body);
        Assert.Contains("\"sub_channel\":\"9999\"", body);
    }

    private sealed class CapturingHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        public Action<HttpRequestMessage> Capture { get; } = capture;

        public Dictionary<string, string> Inner { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Capture(request);
            var content = Inner.TryGetValue(request.RequestUri!.ToString(), out var value) ? value : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
