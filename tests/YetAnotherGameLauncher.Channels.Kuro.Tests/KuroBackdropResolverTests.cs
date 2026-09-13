using YetAnotherGameLauncher.Channels.Kuro;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

/// <summary>
/// 鸣潮背景解析的三级回退：①直连官方运营配置（launcher-config → 背景内容两跳）→ ②注入的缓存查找 →
/// ③注入的帧查找。②③经构造函数委托隔离，①经 StubHttpHandler 隔离。
/// </summary>
public class KuroBackdropResolverTests
{
    private const string IndexUrl = "https://cdn.example.com/launcher/game/G152/10003_H1/index.json";

    private static BackdropRequest Request(string? indexUrl = IndexUrl) => new(
        "wuthering-waves", "kuro", "cn", InstallDir: "D:/games/ww",
        ServerOptions: indexUrl is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [KuroChannelApi.IndexUrlOptionKey] = indexUrl });

    private static KuroBackdropResolver CreateResolver(
        Func<KuroSwitchConfig?>? cached = null,
        Func<string?, string?>? frame = null,
        StubHttpHandler? handler = null,
        string? persistPath = null) =>
        new(new KuroSwitchConfigClient(new HttpClient(handler ?? new StubHttpHandler())),
            cachedConfigLookup: cached, frameLookup: frame, persistPath: persistPath);

    /// <summary>铺好两跳桩：launcher-config 返回背景哈希，zh-Hans 背景内容返回指定投放。</summary>
    private static void MapTwoHop(StubHttpHandler handler, string backgroundJson)
    {
        handler.Map(
            "https://cdn.example.com/launcher/launcher/10003_H1/G152/index.json",
            """{"functionCode":{"background":"HASH123"}}""");
        handler.Map(
            "https://cdn.example.com/launcher/10003_H1/G152/background/HASH123/zh-Hans.json",
            backgroundJson);
    }

    [Fact]
    public async Task GetBackdropUrlAsync_RemoteTwoHop_ReturnsVideoWithPoster()
    {
        var handler = new StubHttpHandler();
        MapTwoHop(handler, """
            {"functionSwitch":1,
             "backgroundFile":"https://cdn.example.com/loop.mp4",
             "backgroundFileType":2,
             "firstFrameImage":"https://cdn.example.com/first.webp"}
            """);
        var persistPath = Path.Combine(Path.GetTempPath(), $"yagl-kuro-bg-{Guid.NewGuid():N}.json");
        var resolver = CreateResolver(cached: () => null, frame: _ => null, handler, persistPath);

        try
        {
            var source = await resolver.GetBackdropUrlAsync(Request());

            Assert.NotNull(source);
            Assert.Equal(BackdropKind.Video, source.Kind);
            Assert.Equal("https://cdn.example.com/loop.mp4", source.Url);
            Assert.Equal("https://cdn.example.com/first.webp", source.PosterUrl);
            // ①级命中后持久化（Chromium 缓存淘汰后的②级兜底来源）
            Assert.True(File.Exists(persistPath));
        }
        finally
        {
            File.Delete(persistPath);
        }
    }

    [Fact]
    public async Task GetBackdropUrlAsync_RemoteStaticFileType_ReturnsImage()
    {
        var handler = new StubHttpHandler();
        MapTwoHop(handler, """
            {"functionSwitch":1,
             "backgroundFile":"https://cdn.example.com/static.webp",
             "backgroundFileType":1,
             "firstFrameImage":"https://cdn.example.com/first.webp"}
            """);
        var resolver = CreateResolver(cached: () => null, frame: _ => null, handler);

        var source = await resolver.GetBackdropUrlAsync(Request());

        Assert.NotNull(source);
        Assert.Equal(BackdropKind.Image, source.Kind);
        Assert.Equal("https://cdn.example.com/static.webp", source.Url);
        Assert.Null(source.PosterUrl); // 静态图投放不带首帧海报
    }

    [Fact]
    public async Task GetBackdropUrlAsync_Offline_CachedSwitchConfig_ReturnsVideoWithPoster()
    {
        var resolver = CreateResolver(
            cached: () => new KuroSwitchConfig("https://cdn/loop.mp4", "https://cdn/first.webp", "echoes"));

        var source = await resolver.GetBackdropUrlAsync(Request(indexUrl: null));

        Assert.NotNull(source);
        Assert.Equal(BackdropKind.Video, source.Kind);
        Assert.Equal("https://cdn/loop.mp4", source.Url);
        Assert.Equal("https://cdn/first.webp", source.PosterUrl);
    }

    [Fact]
    public async Task GetBackdropUrlAsync_Offline_CachedStaticConfig_ReturnsImage()
    {
        // 持久化兜底里的静态图投放（backgroundFileType=1）：Kind 按类型映射而非一律视频
        var resolver = CreateResolver(cached: () =>
            new KuroSwitchConfig("https://cdn/static.webp", null, null, FunctionSwitch: 1, BackgroundFileType: 1));

        var source = await resolver.GetBackdropUrlAsync(Request(indexUrl: null));

        Assert.NotNull(source);
        Assert.Equal(BackdropKind.Image, source.Kind);
        Assert.Equal("https://cdn/static.webp", source.Url);
    }

    [Fact]
    public async Task GetBackdropUrlAsync_NoConfigNoCache_FallsBackToFrame()
    {
        string? framePath = "D:/games/ww/kr_game_cache/animate_bg/home_1.jpg";
        var resolver = CreateResolver(cached: () => null, frame: _ => framePath);

        var source = await resolver.GetBackdropUrlAsync(Request(indexUrl: null));

        Assert.NotNull(source);
        Assert.Equal(BackdropKind.Image, source.Kind);
        Assert.Equal(framePath, source.Url);
        Assert.Null(source.PosterUrl);
    }

    [Fact]
    public async Task GetBackdropUrlAsync_NothingAvailable_ReturnsNull()
    {
        var resolver = CreateResolver(cached: () => null, frame: _ => null);

        Assert.Null(await resolver.GetBackdropUrlAsync(Request(indexUrl: null)));
    }
}
