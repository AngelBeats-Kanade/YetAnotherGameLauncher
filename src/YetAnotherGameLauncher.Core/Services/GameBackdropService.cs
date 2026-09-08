using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using YetAnotherGameLauncher.Core.Abstractions;
using Microsoft.Extensions.Logging;

using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>背景解析结果：本地缓存文件路径（或下载失败时的直链）、类型与海报来源。</summary>
/// <param name="Source">背景本体：本地文件路径；下载失败且有直链时为 http(s) 直链；完全失败为 null。</param>
/// <param name="Kind">背景类型。</param>
/// <param name="PosterSource">视频首帧海报来源（本地缓存路径或直链）；静态图类型为 null。</param>
public sealed record ResolvedBackdrop(string? Source, BackdropKind Kind, string? PosterSource);

/// <summary>
/// 游戏详情页背景的远程解析 + 本地缓存编排。配置文件不携带背景地址：
/// 每次启动向渠道解析器确认当期背景地址，地址变化（版本更新/卡池轮换/运营投放）时重新下载；
/// 离线或下载失败时回退上次缓存，再由调用方回退到更低优先级的来源（如官方启动器本地帧）。
/// 视频背景（可达数十 MB）与首帧海报一并缓存。
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

    private readonly string _cacheRoot = cacheRoot ?? DefaultCacheRoot;

    /// <summary>按游戏串行化解析与下载，避免并发重复下载同一背景。</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gameLocks = new();

    private static string DefaultCacheRoot => Path.Combine(AppPaths.ConfigDirectory, "backdrops");

    /// <summary>
    /// 解析游戏的当期背景，返回本地缓存文件路径、远程直链或解析器给出的本地路径。
    /// 失败链：远程解析失败/下载失败 → 上次缓存 → 远程直链 → null。
    /// </summary>
    public async Task<ResolvedBackdrop?> ResolveAsync(BackdropRequest request, CancellationToken cancellationToken = default)
    {
        if (!resolvers.TryGetValue(request.Channel, out var resolver))
        {
            return null;
        }

        var gate = _gameLocks.GetOrAdd(request.GameId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ResolveCoreAsync(request, resolver, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ResolvedBackdrop?> ResolveCoreAsync(
        BackdropRequest request, IBackdropResolver resolver, CancellationToken cancellationToken)
    {
        BackdropSource? remote;
        try
        {
            remote = await resolver.GetBackdropUrlAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            && !cancellationToken.IsCancellationRequested) // 用户主动取消要向上抛，网络失败/超时才回退缓存
        {
            logger?.LogInformation("Backdrop resolve failed for {GameId}: {Message}", request.GameId, ex.Message);
            remote = null;
        }

        // 解析器直接给出本地文件（如官方启动器本地帧缓存）：无需下载。
        // 用前缀判断而非 Uri.TryCreate——"D:\..." 这类 Windows 路径会被解析成 file:// URI。
        if (remote is not null && !IsHttpUrl(remote.Url))
        {
            return File.Exists(remote.Url)
                ? new ResolvedBackdrop(remote.Url, remote.Kind, remote.PosterUrl)
                : null;
        }

        var cacheDir = Path.Combine(_cacheRoot, Sanitize(request.GameId));
        var meta = ReadMeta(Path.Combine(cacheDir, "meta.json"));

        // 地址或类型变化（版本/卡池轮换/运营投放）→ 重新下载覆盖缓存
        if (remote is not null
            && (!string.Equals(meta?.Url, remote.Url, StringComparison.OrdinalIgnoreCase)
                || meta?.Kind != remote.Kind))
        {
            var downloaded = await TryDownloadAsync(remote, cacheDir, cancellationToken).ConfigureAwait(false);
            if (downloaded is { } download)
            {
                WriteMeta(Path.Combine(cacheDir, "meta.json"), download.Meta);
                return download.Resolved(cacheDir);
            }
        }

        // 缓存命中（远程一致，或远程不可用时的兜底）
        if (meta is { } cached && File.Exists(Path.Combine(cacheDir, cached.File)))
        {
            return new ResolvedBackdrop(
                Path.Combine(cacheDir, cached.File),
                cached.Kind,
                cached.PosterFile is { } poster && File.Exists(Path.Combine(cacheDir, poster))
                    ? Path.Combine(cacheDir, poster)
                    : cached.PosterUrl);
        }

        // 无缓存：有直链就交直链（调用方的加载服务支持 http），否则放弃
        return remote is null ? null : new ResolvedBackdrop(remote.Url, remote.Kind, remote.PosterUrl);
    }

    /// <summary>下载背景本体（视频用流式写盘）与可选的首帧海报；返回缓存元数据或 null。</summary>
    private async Task<(BackdropMeta Meta, Func<string, ResolvedBackdrop> Resolved)?> TryDownloadAsync(
        BackdropSource source, string cacheDir, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);

            var backdropFile = await DownloadToFileAsync(
                source.Url, cacheDir, "backdrop", cancellationToken).ConfigureAwait(false);
            if (backdropFile is null)
            {
                return null;
            }

            // 海报与本体同源投放（官方配置里配套给出）：海报失败不拖垮视频缓存
            var posterFile = source.PosterUrl is null
                ? null
                : await DownloadToFileAsync(source.PosterUrl, cacheDir, "poster", cancellationToken).ConfigureAwait(false);

            return (
                new BackdropMeta(source.Url, backdropFile, source.Kind, source.PosterUrl, posterFile),
                cacheDir => new ResolvedBackdrop(
                    Path.Combine(cacheDir, backdropFile),
                    source.Kind,
                    posterFile is null ? source.PosterUrl : Path.Combine(cacheDir, posterFile)));
        }
        catch (Exception ex) when ((ex is HttpRequestException or IOException or InvalidOperationException
            or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            logger?.LogInformation("Backdrop download failed: {Message}", ex.Message);
            // 枚举本身的失败不能遮盖上面的主异常
            try
            {
                foreach (var stale in Directory.EnumerateFiles(cacheDir, "download-*"))
                {
                    FileUtilities.DeleteQuiet(stale);
                }
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
            }

            return null;
        }
    }

    /// <summary>按来源扩展名下载到缓存目录（先写临时文件再原子改名）；失败返回 null。</summary>
    private async Task<string?> DownloadToFileAsync(
        string url, string cacheDir, string baseName, CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(new Uri(url).AbsolutePath) is { Length: > 1 } e ? e : ".img";
        var tempPath = Path.Combine(cacheDir, $"download-{Guid.NewGuid():N}{ext}");

        // 视频可达数十 MB：流式写盘而非整块读入内存
        using (var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            // CA2007 误报：await using 声明的 DisposeAsync 续体由编译器生成，无法对其追加 ConfigureAwait。
#pragma warning disable CA2007
            await using var http = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = File.Create(tempPath);
#pragma warning restore CA2007
            await http.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var fileName = $"{baseName}{ext}";
        var finalPath = Path.Combine(cacheDir, fileName);
        File.Delete(finalPath);
        File.Move(tempPath, finalPath);
        return fileName;
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

    private static string Sanitize(string value) => string.Concat(value.Where(char.IsLetterOrDigit));

    /// <summary>背景缓存元数据：来源直链 + 本地文件名（+ 类型与海报），用于判断"是否有更新"。</summary>
    /// <param name="Url">来源直链。</param>
    /// <param name="File">背景本体缓存文件名（backdrop&lt;ext&gt;）。</param>
    /// <param name="Kind">背景类型（图/视频）。</param>
    /// <param name="PosterUrl">海报来源直链（视频类型专用）。</param>
    /// <param name="PosterFile">海报缓存文件名（poster&lt;ext&gt;）。</param>
    private sealed record BackdropMeta(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("kind")] BackdropKind Kind = BackdropKind.Image,
        [property: JsonPropertyName("posterUrl")] string? PosterUrl = null,
        [property: JsonPropertyName("posterFile")] string? PosterFile = null);
}
