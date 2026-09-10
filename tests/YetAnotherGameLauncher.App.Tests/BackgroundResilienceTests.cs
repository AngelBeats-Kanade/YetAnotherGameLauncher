using Avalonia;
using Avalonia.Media;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 背景图加载韧性（缓存策略部分，纯逻辑不经 Avalonia 解码）：网络抖动等瞬时失败只短暂缓存
/// （TTL 内直接复用失败结果避免反复打网络），过期后自动重试——启动瞬间的网络未就绪
/// 不该让背景整个会话空白（历史顽疾）。真实解码链路由 DetailPageFeaturesTests 覆盖。
/// </summary>
public sealed class BackgroundResilienceTests
{
    private readonly ManualTimeProvider _clock = new();

    private BackgroundImageService CreateService(TimeSpan? retryInterval = null) =>
        new(new HttpClient(new StubHttpHandler()), _clock, retryInterval ?? TimeSpan.FromMinutes(1));

    [Fact]
    public void FailureWithinInterval_ServedFromCache()
    {
        var service = CreateService();
        var now = _clock.GetUtcNow();

        service.Store("bg://a", null, now); // 记录一次失败

        Assert.Null(service.TryGetCached("bg://a", now.AddSeconds(30))); // TTL 内命中失败缓存
    }

    [Fact]
    public void FailureExpires_RetryAllowed()
    {
        var service = CreateService(retryInterval: TimeSpan.FromSeconds(30));
        var now = _clock.GetUtcNow();

        service.Store("bg://a", null, now);

        Assert.Null(service.TryGetCached("bg://a", now.AddSeconds(31))); // TTL 过期：缓存不再命中
    }

    [Fact]
    public void SuccessIsCachedPermanently_EvenWithZeroTtl()
    {
        var service = CreateService(retryInterval: TimeSpan.Zero);
        var now = _clock.GetUtcNow();
        IImage image = new FakeImage();

        service.Store("bg://a", image, now);
        _clock.Advance(TimeSpan.FromDays(1));

        Assert.Same(image, service.TryGetCached("bg://a", _clock.GetUtcNow())); // 成功条目不走 TTL
    }

    [Fact]
    public void ExpiredFailure_IsRemovedOnLookup()
    {
        var service = CreateService(retryInterval: TimeSpan.FromSeconds(10));
        var now = _clock.GetUtcNow();

        service.Store("bg://a", null, now);
        _clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Null(service.TryGetCached("bg://a", _clock.GetUtcNow())); // 过期即清除

        service.Store("bg://a", new FakeImage(), _clock.GetUtcNow()); // 重新写入成功结果

        Assert.NotNull(service.TryGetCached("bg://a", _clock.GetUtcNow()));
    }

    /// <summary>假 IImage：只为验证缓存策略，不参与渲染。</summary>
    private sealed class FakeImage : IImage
    {
        public Size Size => new(1, 1);

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) =>
            throw new NotSupportedException();
    }
}
