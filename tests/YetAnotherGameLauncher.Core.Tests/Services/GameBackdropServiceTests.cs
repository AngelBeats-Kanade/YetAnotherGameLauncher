using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class GameBackdropServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly StubHttpHandler _handler = new();

    private BackdropRequest Request(string channel = "test", string region = "cn") =>
        new("some-game", channel, region, null, new Dictionary<string, string>());

    private GameBackdropService CreateService(IBackdropResolver resolver) =>
        new(new HttpClient(_handler), new Dictionary<string, IBackdropResolver> { ["test"] = resolver },
            cacheRoot: _tempDir.FilePath("backdrops"));

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public async Task Resolve_RemoteUrl_DownloadsAndCaches()
    {
        _handler.Map("https://cdn.example.com/bg.png", [4, 5, 6, 7]);
        var service = CreateService(new StubResolver(
            _ => new BackdropSource("https://cdn.example.com/bg.png", BackdropKind.Image)));

        var first = await service.ResolveAsync(Request());
        var second = await service.ResolveAsync(Request());

        Assert.NotNull(first);
        Assert.True(File.Exists(first.Source));
        Assert.Equal(await File.ReadAllBytesAsync(first.Source!), (byte[])[4, 5, 6, 7]);
        // 第二次解析命中缓存：不重复下载
        Assert.Equal(first, second);
        Assert.Equal(1, _handler.Requests.Count(r => r.RequestUri == new Uri("https://cdn.example.com/bg.png")));
    }

    [Fact]
    public async Task Resolve_UrlChanged_RetractsNewBackdrop()
    {
        _handler.Map("https://cdn.example.com/old.png", [1]);
        _handler.Map("https://cdn.example.com/new.png", [2]);
        var url = "https://cdn.example.com/old.png";
        var service = CreateService(new StubResolver(
            _ => new BackdropSource(url, BackdropKind.Image)));

        var first = await service.ResolveAsync(Request());
        url = "https://cdn.example.com/new.png";
        var second = await service.ResolveAsync(Request());

        // 路径固定（覆盖式缓存），但内容已被新背景替换
        Assert.Equal(first, second);
        Assert.Equal((byte[])[2], await File.ReadAllBytesAsync(second!.Source!));
    }

    [Fact]
    public async Task Resolve_OldBackdropUndeletable_FallsBackToTimestampedName()
    {
        // Windows 语义：旧背景仍被占用时 Delete/Move 会失败（Linux rename 总能成功）。
        // 跨平台复现"删不掉"：把固定名落点换成同名目录，File.Delete 必抛 UnauthorizedAccessException；
        // 此时应换时间戳备用名落盘，刷新整体不失败，meta 记录新名
        _handler.Map("https://cdn.example.com/old.png", [1]);
        _handler.Map("https://cdn.example.com/new.png", [2]);
        var url = "https://cdn.example.com/old.png";
        var service = CreateService(new StubResolver(
            _ => new BackdropSource(url, BackdropKind.Image)));

        var first = await service.ResolveAsync(Request());
        File.Delete(first!.Source!);
        Directory.CreateDirectory(first.Source!); // 用目录占住固定名落点
        url = "https://cdn.example.com/new.png";
        var second = await service.ResolveAsync(Request());

        Assert.NotNull(second);
        Assert.NotEqual(first.Source, second.Source);
        Assert.Contains("backdrop-", Path.GetFileName(second.Source));
        Assert.Equal((byte[])[2], await File.ReadAllBytesAsync(second.Source!));
    }

    [Fact]
    public async Task Resolve_VideoSource_DownloadsBackdropAndPoster()
    {
        _handler.Map("https://cdn.example.com/bg.mp4", (byte[])[1, 2, 3, 4]);
        _handler.Map("https://cdn.example.com/poster.webp", (byte[])[8, 9]);
        var service = CreateService(new StubResolver(
            _ => new BackdropSource("https://cdn.example.com/bg.mp4", BackdropKind.Video, "https://cdn.example.com/poster.webp")));

        var resolved = await service.ResolveAsync(Request());

        Assert.Equal(BackdropKind.Video, resolved!.Kind);
        Assert.Equal(".mp4", Path.GetExtension(resolved.Source));
        Assert.True(File.Exists(resolved.Source));
        Assert.True(File.Exists(resolved.PosterSource));
        Assert.Equal((byte[])[1, 2, 3, 4], await File.ReadAllBytesAsync(resolved.Source!));
        Assert.Equal((byte[])[8, 9], await File.ReadAllBytesAsync(resolved.PosterSource!));
    }

    [Fact]
    public async Task Resolve_VideoCached_ReuseWithoutRedownload()
    {
        _handler.Map("https://cdn.example.com/bg.mp4", (byte[])[1, 2, 3, 4]);
        _handler.Map("https://cdn.example.com/poster.webp", (byte[])[8, 9]);
        var source = new BackdropSource(
            "https://cdn.example.com/bg.mp4", BackdropKind.Video, "https://cdn.example.com/poster.webp");
        var service = CreateService(new StubResolver(_ => source));

        var first = await service.ResolveAsync(Request());
        var second = await service.ResolveAsync(Request());

        Assert.Equal(first, second);
        // 本体一次 + 海报一次：第二轮解析完全走缓存
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Resolve_KindChanged_RetractsNewBackdrop()
    {
        // 同一 URL 从图变视频（官方从投图改投视频）：kind 变化也要重新下载
        _handler.Map("https://cdn.example.com/media", (byte[])[7, 7]);
        var service = CreateService(new StubResolver(
            _ => new BackdropSource("https://cdn.example.com/media", BackdropKind.Image)));
        await service.ResolveAsync(Request());

        var video = await CreateService(new StubResolver(
            _ => new BackdropSource("https://cdn.example.com/media", BackdropKind.Video, "https://cdn.example.com/media"))).ResolveAsync(Request());

        Assert.Equal(BackdropKind.Video, video!.Kind);
    }

    [Fact]
    public async Task Resolve_ResolverFails_FallsBackToCachedFile()
    {
        _handler.Map("https://cdn.example.com/bg.png", [9]);
        var url = "https://cdn.example.com/bg.png";
        var resolver = new StubResolver(_ => new BackdropSource(url, BackdropKind.Image));
        var service = CreateService(resolver);
        var cached = await service.ResolveAsync(Request());

        resolver.Resolver = _ => null; // 模拟离线：远程解析不可用
        var fallback = await service.ResolveAsync(Request());

        Assert.Equal(cached, fallback);
    }

    [Fact]
    public async Task Resolve_UnknownChannel_ReturnsNull()
    {
        var service = CreateService(new StubResolver(
            _ => new BackdropSource("https://cdn.example.com/bg.png", BackdropKind.Image)));

        var source = await service.ResolveAsync(Request(channel: "unregistered"));

        Assert.Null(source);
    }

    [Fact]
    public async Task Resolve_VersionUnchanged_SkipsResolverAndNetwork()
    {
        _handler.Map("https://cdn.example.com/bg.png", [1, 2, 3]);
        var resolver = new StubResolver(_ => new BackdropSource("https://cdn.example.com/bg.png", BackdropKind.Image));
        var service = CreateService(resolver);

        var first = await service.ResolveAsync(Request(), "1.0.0");
        var second = await service.ResolveAsync(Request(), "1.0.0");

        // 版本一致：第二次解析零解析器调用、零下载（版本门控直接命中磁盘缓存）
        Assert.Equal(1, resolver.ResolveCount);
        Assert.Equal(1, _handler.Requests.Count(r => r.RequestUri == new Uri("https://cdn.example.com/bg.png")));
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Resolve_VersionChanged_ReinvokesResolver_ButDoesNotRedownloadSameUrl()
    {
        _handler.Map("https://cdn.example.com/bg.png", [1, 2, 3]);
        var resolver = new StubResolver(_ => new BackdropSource("https://cdn.example.com/bg.png", BackdropKind.Image));
        var service = CreateService(resolver);

        await service.ResolveAsync(Request(), "1.0.0");
        var second = await service.ResolveAsync(Request(), "2.0.0");

        // 版本变化触发重新解析确认地址；地址未变则不重下，仅升级元数据里的版本
        Assert.Equal(2, resolver.ResolveCount);
        Assert.Equal(1, _handler.Requests.Count(r => r.RequestUri == new Uri("https://cdn.example.com/bg.png")));
        Assert.NotNull(second);
        Assert.Equal("2.0.0", service.GetCachedGameVersion("some-game"));
    }

    [Fact]
    public async Task Resolve_RegionChanged_ReinvokesResolver_EvenIfVersionSame()
    {
        _handler.Map("https://cdn.example.com/bg.png", [1]);
        _handler.Map("https://cdn.example.com/bg-global.png", [2]);
        var url = "https://cdn.example.com/bg.png";
        var resolver = new StubResolver(_ => new BackdropSource(url, BackdropKind.Image));
        var service = CreateService(resolver);
        await service.ResolveAsync(Request(), "1.0.0");

        url = "https://cdn.example.com/bg-global.png";
        await service.ResolveAsync(Request(region: "global"), "1.0.0");

        // 区域变化（语言切换换服）：即使版本一致也要重新解析另一区域的投放
        Assert.Equal(2, resolver.ResolveCount);
    }

    [Fact]
    public async Task Resolve_LegacyMetaWithoutVersion_InvokesResolverOnceThenGates()
    {
        // 旧格式缓存（无 region/gameVersion 字段）：门控未命中解析一次后升级元数据，此后可门控
        var cacheDir = _tempDir.FilePath("backdrops", "some-game");
        Directory.CreateDirectory(cacheDir);
        await File.WriteAllTextAsync(Path.Combine(cacheDir, "meta.json"),
            """{"url":"https://cdn.example.com/bg.png","file":"backdrop.png","kind":"Image"}""");
        await File.WriteAllBytesAsync(Path.Combine(cacheDir, "backdrop.png"), [5]);
        _handler.Map("https://cdn.example.com/bg.png", [5]);
        var resolver = new StubResolver(_ => new BackdropSource("https://cdn.example.com/bg.png", BackdropKind.Image));
        var service = CreateService(resolver);

        var first = await service.ResolveAsync(Request(), "1.0.0");
        var second = await service.ResolveAsync(Request(), "1.0.0");

        Assert.Equal(1, resolver.ResolveCount); // 升级后第二次门控命中
        Assert.Equal(0, _handler.Requests.Count(r => r.RequestUri == new Uri("https://cdn.example.com/bg.png"))); // 地址未变不重下
        Assert.Equal("1.0.0", service.GetCachedGameVersion("some-game"));
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ResolveCached_Hit_ReturnsFile_WithoutResolverOrNetwork()
    {
        _handler.Map("https://cdn.example.com/bg.mp4", [1, 2, 3, 4]);
        _handler.Map("https://cdn.example.com/poster.webp", [8, 9]);
        var service = CreateService(new StubResolver(
            _ => new BackdropSource("https://cdn.example.com/bg.mp4", BackdropKind.Video, "https://cdn.example.com/poster.webp")));
        await service.ResolveAsync(Request(), "1.0.0");
        _handler.Requests.Clear();

        var cached = await service.ResolveCachedAsync(Request());

        Assert.NotNull(cached);
        Assert.True(File.Exists(cached.Source));
        Assert.Equal(BackdropKind.Video, cached.Kind);
        Assert.True(File.Exists(cached.PosterSource));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Resolve_PosterDownloadFails_VideoCacheStillLands()
    {
        // 海报 404：本体缓存照常落盘（海报回退直链），不因海报失败整个作废
        _handler.Map("https://cdn.example.com/bg.mp4", [1, 2, 3, 4]);
        var service = CreateService(new StubResolver(
            _ => new BackdropSource("https://cdn.example.com/bg.mp4", BackdropKind.Video, "https://cdn.example.com/poster-missing.webp")));

        var resolved = await service.ResolveAsync(Request(), "1.0.0");

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved.Source));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(resolved.Source)!, "meta.json")));
        Assert.Equal("https://cdn.example.com/poster-missing.webp", resolved.PosterSource);
    }

    [Fact]
    public async Task ResolveCached_Miss_ReturnsNull()
    {
        var service = CreateService(new StubResolver(
            _ => new BackdropSource("https://cdn.example.com/bg.png", BackdropKind.Image)));

        Assert.Null(await service.ResolveCachedAsync(Request()));
    }

    [Fact]
    public async Task GetCachedGameVersion_NoCache_ReturnsNull()
    {
        var service = CreateService(new StubResolver(
            _ => new BackdropSource("https://cdn.example.com/bg.png", BackdropKind.Image)));

        Assert.Null(service.GetCachedGameVersion("some-game"));
    }

    [Fact]
    public async Task CorruptCacheMeta_TreatedAsNoCache()
    {
        // 审计缺口（2026-09-19）：缓存元数据损坏（写一半崩溃等）→ 按无缓存兜底：
        // 版本查询 null、离线缓存解析 null（回退主题渐变），解析链不崩
        var cacheDir = _tempDir.FilePath("backdrops", "some-game"); // 目录名按游戏 id 保形清洗
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "meta.json"), "{not-json");

        var service = CreateService(new StubResolver(_ => null));

        Assert.Null(service.GetCachedGameVersion("some-game"));
        Assert.Null(await service.ResolveCachedAsync(Request()));
    }

    private sealed class StubResolver(Func<string, BackdropSource?> resolve) : IBackdropResolver
    {
        public Func<string, BackdropSource?> Resolver { get; set; } = resolve;

        /// <summary>解析器被调用次数（验证版本门控是否跳过远程解析）。</summary>
        public int ResolveCount { get; private set; }

        public Task<BackdropSource?> GetBackdropUrlAsync(BackdropRequest request, CancellationToken cancellationToken = default)
        {
            ResolveCount++;
            return Task.FromResult(Resolver(request.Region));
        }
    }
}

public class GameBackdropCacheIsolationTests : IDisposable
{
    private sealed class StaticResolver(Func<string, BackdropSource?> resolve) : IBackdropResolver
    {
        public Task<BackdropSource?> GetBackdropUrlAsync(BackdropRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(resolve(request.Region));
    }

    private readonly TempDir _tempDir = new();
    private readonly StubHttpHandler _handler = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public async Task Resolve_GameIdsDifferingOnlyInSeparator_DoNotShareCacheDirectory()
    {
        // 回归（2026-09-20 三审）：缓存目录清洗曾抹掉 -_. ——"game-a"/"game.a"/"gamea" 映射到
        // 同一缓存目录，不同游戏互踩背景缓存与版本门控
        _handler.Map("https://cdn.example.com/bg.png", [1]);
        var service = new GameBackdropService(
            new HttpClient(_handler),
            new Dictionary<string, IBackdropResolver>
            {
                ["test"] = new StaticResolver(_ => new BackdropSource("https://cdn.example.com/bg.png", BackdropKind.Image)),
            },
            cacheRoot: _tempDir.FilePath("backdrops"));

        var first = await service.ResolveAsync(new BackdropRequest("game-a", "test", "cn", null, new Dictionary<string, string>()));
        var second = await service.ResolveAsync(new BackdropRequest("game.a", "test", "cn", null, new Dictionary<string, string>()));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Source, second!.Source);
    }
}
