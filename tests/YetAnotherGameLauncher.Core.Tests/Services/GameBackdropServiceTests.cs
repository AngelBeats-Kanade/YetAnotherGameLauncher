using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class GameBackdropServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly StubHttpHandler _handler = new();

    private BackdropRequest Request(string channel = "test") =>
        new("some-game", channel, "cn", null, new Dictionary<string, string>());

    private GameBackdropService CreateService(IBackdropResolver resolver) =>
        new(new HttpClient(_handler), new Dictionary<string, IBackdropResolver> { ["test"] = resolver },
            cacheRoot: _tempDir.FilePath("backdrops"));

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public async Task Resolve_LocalPathFromResolver_ReturnedAsIs()
    {
        var imagePath = _tempDir.FilePath("frame.jpg");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
        var resolver = new StubResolver(_ => imagePath);

        var source = await CreateService(resolver).ResolveAsync(Request());

        Assert.Equal(imagePath, source);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Resolve_RemoteUrl_DownloadsAndCaches()
    {
        _handler.Map("https://cdn.example.com/bg.png", [4, 5, 6, 7]);
        var service = CreateService(new StubResolver(_ => "https://cdn.example.com/bg.png"));

        var first = await service.ResolveAsync(Request());
        var second = await service.ResolveAsync(Request());

        Assert.NotNull(first);
        Assert.True(File.Exists(first));
        Assert.Equal(await File.ReadAllBytesAsync(first!), (byte[])[4, 5, 6, 7]);
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
        var service = CreateService(new StubResolver(_ => url));

        var first = await service.ResolveAsync(Request());
        url = "https://cdn.example.com/new.png";
        var second = await service.ResolveAsync(Request());

        // 路径固定（覆盖式缓存），但内容已被新背景替换
        Assert.Equal(first, second);
        Assert.Equal((byte[])[2], await File.ReadAllBytesAsync(second!));
    }

    [Fact]
    public async Task Resolve_ResolverFails_FallsBackToCachedFile()
    {
        _handler.Map("https://cdn.example.com/bg.png", [9]);
        var url = "https://cdn.example.com/bg.png";
        var resolver = new StubResolver(_ => url);
        var service = CreateService(resolver);
        var cached = await service.ResolveAsync(Request());

        resolver.Resolver = _ => null; // 模拟离线：远程解析不可用
        var fallback = await service.ResolveAsync(Request());

        Assert.Equal(cached, fallback);
    }

    [Fact]
    public async Task Resolve_UnknownChannel_ReturnsNull()
    {
        var service = CreateService(new StubResolver(_ => "https://cdn.example.com/bg.png"));

        var source = await service.ResolveAsync(Request(channel: "unregistered"));

        Assert.Null(source);
    }

    private sealed class StubResolver(Func<string, string?> resolve) : IBackdropResolver
    {
        public Func<string, string?> Resolver { get; set; } = resolve;

        public Task<string?> GetBackdropUrlAsync(BackdropRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Resolver(request.Region));
    }
}
