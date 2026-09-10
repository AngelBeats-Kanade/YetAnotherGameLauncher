using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Media.Imaging;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 游戏详情页背景图加载：支持 http(s) URL 与本地路径，会话内按来源缓存；
/// 任何失败（离线、文件不存在、解码失败）都返回 null 并由界面回退到主题渐变。
/// 成功结果永久缓存；失败结果只短暂缓存（TTL）——启动瞬间的网络抖动不该让
/// 背景整个会话空白，过期后自动重试（历史顽疾修复）。
/// </summary>
public sealed class BackgroundImageService(
    HttpClient httpClient,
    TimeProvider? timeProvider = null,
    TimeSpan? failureRetryInterval = null)
{
    /// <summary>失败结果的缓存时长：过期后重新尝试加载。</summary>
    private readonly TimeSpan _failureRetryInterval = failureRetryInterval ?? TimeSpan.FromMinutes(1);

    /// <summary>时间源（可注入虚拟时钟，测试确定）。</summary>
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private readonly Dictionary<string, CacheEntry> _cache = [];

    /// <summary>按来源加载并解码图片；失败返回 null（短暂缓存，见 <see cref="_failureRetryInterval"/>）。</summary>
    public async Task<IImage?> LoadAsync(string? source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        if (TryGetCached(source, _time.GetUtcNow()) is { } cached)
        {
            return cached;
        }

        IImage? image = null;
        try
        {
            var bytes = await FetchAsync(source, cancellationToken);
            image = new Bitmap(new MemoryStream(bytes));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or ArgumentException)
        {
            // 静默回退：背景图是纯装饰
        }

        Store(source, image, _time.GetUtcNow());
        return image;
    }

    /// <summary>查询缓存：成功条目永久有效；失败条目仅 TTL 内命中。过期条目顺带清除。</summary>
    internal IImage? TryGetCached(string source, DateTimeOffset now)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(source, out var cached))
            {
                return null;
            }

            if (cached.Success || now - cached.At < _failureRetryInterval)
            {
                return cached.Image;
            }

            _cache.Remove(source); // 失败缓存过期：丢弃，走重新加载
            return null;
        }
    }

    /// <summary>写入缓存条目（成功永久、失败按 TTL）。</summary>
    internal void Store(string source, IImage? image, DateTimeOffset now)
    {
        lock (_cache)
        {
            _cache[source] = new CacheEntry(image, now, image is not null);
        }
    }

    /// <summary>按来源类型取原始字节：内置资源 / http(s) 下载 / 本地文件读取。</summary>
    private async Task<byte[]> FetchAsync(string source, CancellationToken cancellationToken)
    {
        if (source.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
        {
            // 应用内置资源（随包分发的官方图标等），离线可用
            await using var stream = AssetLoader.Open(new Uri(source, UriKind.Absolute));
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken);
            return ms.ToArray();
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            return await httpClient.GetByteArrayAsync(source, cancellationToken);
        }

        return await File.ReadAllBytesAsync(source, cancellationToken);
    }

    /// <summary>缓存条目：解码结果 + 写入时间 + 是否成功（成功永久、失败按 TTL）。</summary>
    private sealed record CacheEntry(IImage? Image, DateTimeOffset At, bool Success);
}
