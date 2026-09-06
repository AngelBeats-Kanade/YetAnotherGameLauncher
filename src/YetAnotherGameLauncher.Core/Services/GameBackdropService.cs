using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using YetAnotherGameLauncher.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// 游戏详情页背景的远程解析 + 本地缓存编排。配置文件不携带背景地址：
/// 每次启动向渠道解析器确认当期背景地址，地址变化（版本更新/卡池轮换）时重新下载；
/// 离线或下载失败时回退上次缓存，再由调用方回退到更低优先级的来源（如官方启动器本地帧）。
/// </summary>
public sealed class GameBackdropService(
    HttpClient httpClient,
    IReadOnlyDictionary<string, IBackdropResolver> resolvers,
    ILogger? logger = null,
    string? cacheRoot = null)
{
    private static readonly JsonSerializerOptions MetaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _httpClient = httpClient;
    private readonly IReadOnlyDictionary<string, IBackdropResolver> _resolvers = resolvers;
    private readonly string _cacheRoot = cacheRoot ?? DefaultCacheRoot;

    /// <summary>按游戏串行化解析与下载，避免并发重复下载同一背景。</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gameLocks = new();

    private static string DefaultCacheRoot => Path.Combine(AppPaths.ConfigDirectory, "backdrops");

    /// <summary>
    /// 解析游戏的当期背景，返回本地缓存文件路径、远程直链或解析器给出的本地路径。
    /// 失败链：远程解析失败/下载失败 → 上次缓存 → 远程直链 → null。
    /// </summary>
    public async Task<string?> ResolveAsync(BackdropRequest request, CancellationToken cancellationToken = default)
    {
        if (!_resolvers.TryGetValue(request.Channel, out var resolver))
        {
            return null;
        }

        var gate = _gameLocks.GetOrAdd(request.GameId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ResolveCoreAsync(request, resolver, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> ResolveCoreAsync(
        BackdropRequest request, IBackdropResolver resolver, CancellationToken cancellationToken)
    {
        string? remoteUrl;
        try
        {
            remoteUrl = await resolver.GetBackdropUrlAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger?.LogDebug("Backdrop resolve failed for {GameId}: {Message}", request.GameId, ex.Message);
            remoteUrl = null;
        }

        // 解析器直接给出本地文件（如官方启动器本地帧缓存）：无需下载。
        // 用前缀判断而非 Uri.TryCreate——"D:\..." 这类 Windows 路径会被解析成 file:// URI。
        if (remoteUrl is not null && !IsHttpUrl(remoteUrl))
        {
            return File.Exists(remoteUrl) ? remoteUrl : null;
        }

        if (remoteUrl is not null
            && !Uri.TryCreate(remoteUrl, UriKind.Absolute, out var remoteUri))
        {
            remoteUrl = null;
        }

        var cacheDir = Path.Combine(_cacheRoot, Sanitize(request.GameId));
        var meta = ReadMeta(Path.Combine(cacheDir, "meta.json"));

        // 地址变化（版本/卡池轮换）→ 重新下载覆盖缓存
        if (remoteUrl is not null && !string.Equals(meta?.Url, remoteUrl, StringComparison.OrdinalIgnoreCase))
        {
            var downloaded = await TryDownloadAsync(remoteUrl, cacheDir, cancellationToken);
            if (downloaded is not null)
            {
                WriteMeta(Path.Combine(cacheDir, "meta.json"), new BackdropMeta(remoteUrl, downloaded));
                return Path.Combine(cacheDir, downloaded);
            }
        }

        // 缓存命中（远程一致，或远程不可用时的兜底）
        if (meta?.File is { } cachedFile && File.Exists(Path.Combine(cacheDir, cachedFile)))
        {
            return Path.Combine(cacheDir, cachedFile);
        }

        // 无缓存：有直链就交直链（调用方的加载服务支持 http），否则放弃
        return remoteUrl;
    }

    private async Task<string?> TryDownloadAsync(string url, string cacheDir, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            var ext = Path.GetExtension(new Uri(url).AbsolutePath) is { Length: > 1 } e ? e : ".img";
            var tempPath = Path.Combine(cacheDir, $"download-{Guid.NewGuid():N}{ext}");
            var bytes = await _httpClient.GetByteArrayAsync(url, cancellationToken);
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);

            var fileName = $"backdrop{ext}";
            var finalPath = Path.Combine(cacheDir, fileName);
            File.Delete(finalPath);
            File.Move(tempPath, finalPath);
            return fileName;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
            or TaskCanceledException)
        {
            logger?.LogDebug("Backdrop download failed: {Message}", ex.Message);
            try
            {
                foreach (var stale in Directory.EnumerateFiles(cacheDir, "download-*"))
                {
                    File.Delete(stale);
                }
            }
            catch (IOException)
            {
            }

            return null;
        }
    }

    private static BackdropMeta? ReadMeta(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<BackdropMeta>(json, MetaJsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static void WriteMeta(string path, BackdropMeta meta)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(meta, MetaJsonOptions));
        }
        catch (IOException)
        {
            // 缓存元数据写失败不致命：下次启动重试
        }
    }

    private static bool IsHttpUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>背景缓存元数据：来源直链 + 本地文件名，用于判断"是否有更新"。</summary>
    private sealed record BackdropMeta(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("file")] string File);
}
