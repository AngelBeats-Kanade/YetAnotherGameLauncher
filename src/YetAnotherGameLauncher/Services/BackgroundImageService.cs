using System.Security.Cryptography;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using YetAnotherGameLauncher.Core;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 游戏详情页背景图加载：支持 http(s) URL 与本地路径，两级缓存——
/// 会话内内存缓存 + http 来源的磁盘缓存（跨会话免下载，启动直接读盘）；
/// 任何失败（离线、文件不存在、解码失败）都返回 null 并由界面回退到主题渐变。
/// 成功结果永久缓存；失败结果只短暂缓存（TTL）——启动瞬间的网络抖动不该让
/// 背景整个会话空白，过期后自动重试（历史顽疾修复）。
/// 内置资源（avares://）与本地文件本就在磁盘上，不经过磁盘缓存层。
/// </summary>
public sealed class BackgroundImageService(
    HttpClient httpClient,
    TimeProvider? timeProvider = null,
    TimeSpan? failureRetryInterval = null,
    string? diskCacheRoot = null)
{
    /// <summary>失败结果的缓存时长：过期后重新尝试加载。</summary>
    private readonly TimeSpan _failureRetryInterval = failureRetryInterval ?? TimeSpan.FromMinutes(1);

    /// <summary>时间源（可注入虚拟时钟，测试确定）。</summary>
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>http 来源的磁盘缓存根目录。</summary>
    private readonly string _diskCacheRoot = diskCacheRoot ?? DefaultDiskCacheRoot;

    private static string DefaultDiskCacheRoot => Path.Combine(AppPaths.ConfigDirectory, "image-cache");

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

        var image = await DecodeOrFailureAsync(source, bypassDiskCache: false, cancellationToken);
        Store(source, image, _time.GetUtcNow());
        return image;
    }

    /// <summary>
    /// 绕过内存与磁盘缓存强制重取（游戏版本变化后刷新 http 图标；avares/本地文件等效重读），
    /// 结果回写两级缓存。失败返回 null（失败 TTL 语义与 LoadAsync 一致）。
    /// </summary>
    public async Task<IImage?> ReloadAsync(string? source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var image = await DecodeOrFailureAsync(source, bypassDiskCache: true, cancellationToken);
        Store(source, image, _time.GetUtcNow());
        return image;
    }

    /// <summary>取字节并解码，失败返回 null（网络/磁盘/解码异常都在此吞掉：装饰性资源）。</summary>
    private async Task<IImage?> DecodeOrFailureAsync(string source, bool bypassDiskCache, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await FetchAsync(source, bypassDiskCache, cancellationToken);
            return new Bitmap(new MemoryStream(bytes));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or ArgumentException)
        {
            // 解码/下载失败时坏字节可能已作为缓存落盘（如 CDN 返回 200 + HTML 错误页）：
            // 删除毒化条目让下次加载回落网络重下，否则磁盘命中路径会永久卡死在坏字节上
            TryInvalidateDiskCache(source);

            // 静默回退：背景图是纯装饰
            return null;
        }
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

    /// <summary>按来源类型取原始字节：内置资源 / 本地文件直读；http(s) 经磁盘缓存层（bypassDiskCache 时强制重下）。</summary>
    private async Task<byte[]> FetchAsync(string source, bool bypassDiskCache, CancellationToken cancellationToken)
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
            if (!bypassDiskCache)
            {
                var cachePath = DiskCachePath(source);
                try
                {
                    if (File.Exists(cachePath))
                    {
                        return await File.ReadAllBytesAsync(cachePath, cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 缓存读失败不致命：走网络重下
                }
            }

            var bytes = await httpClient.GetByteArrayAsync(source, cancellationToken);
            TryWriteDiskCache(source, bytes);
            return bytes;
        }

        return await File.ReadAllBytesAsync(source, cancellationToken);
    }

    /// <summary>http 来源的磁盘缓存路径：URL 的 SHA256 + 按来源扩展名（URL 变化即新键，配置改图标自动失效）。</summary>
    private string DiskCachePath(string source)
    {
        var ext = Path.GetExtension(new Uri(source).AbsolutePath) is { Length: > 1 } e ? e : ".img";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        return Path.Combine(_diskCacheRoot, hash + ext);
    }

    /// <summary>删除 http 来源的磁盘缓存条目（字节无效/下载到坏数据时调用，防止毒化条目永久命中）。</summary>
    private void TryInvalidateDiskCache(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return;
        }

        try
        {
            var path = DiskCachePath(source);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>把图像字节写入磁盘缓存（临时文件 + 原子改名；写前清理同源遗留临时文件，写失败静默不致命）。</summary>
    private void TryWriteDiskCache(string source, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(_diskCacheRoot);
            var path = DiskCachePath(source);

            // 同源（同 URL）上次崩溃在 Move 前遗留的临时半成品先清掉；不同 URL 的并发写互不干扰
            var stalePattern = Path.GetFileName(path) + ".download-*";
            foreach (var stale in Directory.EnumerateFiles(_diskCacheRoot, stalePattern))
            {
                File.Delete(stale);
            }

            var tempPath = $"{path}.download-{Guid.NewGuid():N}";
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>缓存条目：解码结果 + 写入时间 + 是否成功（成功永久、失败按 TTL）。</summary>
    private sealed record CacheEntry(IImage? Image, DateTimeOffset At, bool Success);
}
