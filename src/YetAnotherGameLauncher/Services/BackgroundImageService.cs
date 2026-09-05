using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 游戏详情页背景图加载：支持 http(s) URL 与本地路径，会话内按来源缓存；
/// 任何失败（离线、文件不存在、解码失败）都返回 null 并由界面回退到主题渐变。
/// </summary>
public sealed class BackgroundImageService(HttpClient httpClient)
{
    private readonly Dictionary<string, IImage?> _cache = [];

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

    private async Task<byte[]> FetchAsync(string source, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            return await httpClient.GetByteArrayAsync(source, cancellationToken);
        }

        return await File.ReadAllBytesAsync(source, cancellationToken);
    }
}
