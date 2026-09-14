using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

/// <summary>
/// 鸣潮背景解析：直连官方运营配置（launcher-config → 背景内容两跳，经 StubHttpHandler 隔离），
/// 与终末地一致的单级模型——视频投放带首帧图兜底，解析失败返回 null。
/// </summary>
public class KuroBackdropResolverTests
{
    private const string IndexUrl = "https://cdn.example.com/launcher/game/G152/10003_H1/index.json";

    private static BackdropRequest Request(string? indexUrl = IndexUrl) => new(
        "wuthering-waves", "kuro", "cn", InstallDir: "D:/games/ww",
        ServerOptions: indexUrl is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [KuroChannelApi.IndexUrlOptionKey] = indexUrl });

    private static KuroBackdropResolver CreateResolver(StubHttpHandler? handler = null) =>
        new(new KuroSwitchConfigClient(new HttpClient(handler ?? new StubHttpHandler())));

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
        var resolver = CreateResolver(handler);

        var source = await resolver.GetBackdropUrlAsync(Request());

        Assert.NotNull(source);
        Assert.Equal(BackdropKind.Video, source.Kind);
        Assert.Equal("https://cdn.example.com/loop.mp4", source.Url);
        Assert.Equal("https://cdn.example.com/first.webp", source.PosterUrl);
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
        var resolver = CreateResolver(handler);

        var source = await resolver.GetBackdropUrlAsync(Request());

        Assert.NotNull(source);
        Assert.Equal(BackdropKind.Image, source.Kind);
        Assert.Equal("https://cdn.example.com/static.webp", source.Url);
        Assert.Null(source.PosterUrl); // 静态图投放不带首帧海报
    }

    [Fact]
    public async Task GetBackdropUrlAsync_NothingAvailable_ReturnsNull()
    {
        var resolver = CreateResolver();

        Assert.Null(await resolver.GetBackdropUrlAsync(Request(indexUrl: null)));
    }
}
