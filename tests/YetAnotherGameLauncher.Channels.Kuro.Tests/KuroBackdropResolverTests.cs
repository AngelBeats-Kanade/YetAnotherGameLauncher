using YetAnotherGameLauncher.Channels.Kuro;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

/// <summary>鸣潮背景解析的三级回退：直连配置 → 注入的缓存查找 → 注入的帧查找。</summary>
public class KuroBackdropResolverTests
{
    private static BackdropRequest Request() => new(
        "wuthering-waves", "kuro", "cn", InstallDir: "D:/games/ww",
        ServerOptions: new Dictionary<string, string>());

    private static KuroBackdropResolver CreateResolver(
        Func<KuroSwitchConfig?>? cached = null,
        Func<string?, string?>? frame = null) =>
        new(new KuroSwitchConfigClient(new HttpClient(new StubHttpHandler())),
            cachedConfigLookup: cached, frameLookup: frame);

    [Fact]
    public async Task GetBackdropUrlAsync_Offline_CachedSwitchConfig_ReturnsVideoWithPoster()
    {
        var resolver = CreateResolver(
            cached: () => new KuroSwitchConfig("https://cdn/loop.mp4", "https://cdn/first.webp", "echoes"));

        var source = await resolver.GetBackdropUrlAsync(Request());

        Assert.NotNull(source);
        Assert.Equal(BackdropKind.Video, source.Kind);
        Assert.Equal("https://cdn/loop.mp4", source.Url);
        Assert.Equal("https://cdn/first.webp", source.PosterUrl);
    }

    [Fact]
    public async Task GetBackdropUrlAsync_NoConfigNoCache_FallsBackToFrame()
    {
        string? framePath = "D:/games/ww/kr_game_cache/animate_bg/home_1.jpg";
        var resolver = CreateResolver(cached: () => null, frame: _ => framePath);

        var source = await resolver.GetBackdropUrlAsync(Request());

        Assert.NotNull(source);
        Assert.Equal(BackdropKind.Image, source.Kind);
        Assert.Equal(framePath, source.Url);
        Assert.Null(source.PosterUrl);
    }

    [Fact]
    public async Task GetBackdropUrlAsync_NothingAvailable_ReturnsNull()
    {
        var resolver = CreateResolver(cached: () => null, frame: _ => null);

        Assert.Null(await resolver.GetBackdropUrlAsync(Request()));
    }
}
