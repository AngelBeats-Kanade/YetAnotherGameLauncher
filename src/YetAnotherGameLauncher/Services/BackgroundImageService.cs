using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Media.Imaging;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 游戏详情页背景图加载：支持 http(s) URL 与本地路径，会话内按来源缓存；
/// 任何失败（离线、文件不存在、解码失败）都返回 null 并由界面回退到主题渐变。
/// </summary>
public sealed class BackgroundImageService(HttpClient httpClient)
{
    private readonly Dictionary<string, IImage?> _cache = [];

    /// <summary>按来源加载并解码图片（会话内按来源缓存，含失败结果）；失败返回 null。</summary>
    public async Task<IImage?> LoadAsync(string? source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        lock (_cache)
        {
            if (_cache.TryGetValue(source, out var cached))
            {
                return cached;
            }
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

        lock (_cache)
        {
            _cache[source] = image;
        }

        return image;
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
}
