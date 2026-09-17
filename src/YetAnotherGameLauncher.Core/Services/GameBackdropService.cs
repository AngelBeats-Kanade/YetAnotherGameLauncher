using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>背景解析结果：本地缓存文件路径（或下载失败时的直链）、类型与海报来源。</summary>
/// <param name="Source">背景本体：本地文件路径；下载失败且有直链时为 http(s) 直链；完全失败为 null。</param>
/// <param name="Kind">背景类型。</param>
/// <param name="PosterSource">视频首帧海报来源（本地缓存路径或直链）；静态图类型为 null。</param>
public sealed record ResolvedBackdrop(string? Source, BackdropKind Kind, string? PosterSource);

/// <summary>
/// 游戏详情页背景的远程解析 + 本地缓存编排。配置文件不携带背景地址：
/// 缓存元数据记录抓取时的区域与游戏版本，两者均未变化时直接使用缓存（零网络），
/// 版本更新后的首次解析才向渠道确认当期地址、地址变化时重新下载（严格跟随版本，
/// 卡池轮换等运营投放不触发刷新）；离线或下载失败时回退上次缓存，
/// 均不可用时由调用方回退主题渐变。视频背景（可达数十 MB）与首帧海报一并缓存。
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
    /// 版本门控：缓存元数据记录的游戏版本与区域均与传入值一致时直接返回缓存（零网络），
    /// 背景只在游戏版本更新后的首次解析时重新获取。失败链：远程解析失败/下载失败 → 上次缓存 → 远程直链 → null。
    /// </summary>
    public async Task<ResolvedBackdrop?> ResolveAsync(
        BackdropRequest request, string? gameVersion = null, CancellationToken cancellationToken = default)
    {
        if (!resolvers.TryGetValue(request.Channel, out var resolver))
        {
            return null;
        }

        var gate = _gameLocks.GetOrAdd(request.GameId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cacheDir = Path.Combine(_cacheRoot, Sanitize(request.GameId));
            var meta = ReadMeta(Path.Combine(cacheDir, "meta.json"));

            // 版本门控（缓存直读，零网络）：区域与游戏版本都未变化时不再向渠道确认当期地址，
            // 卡池轮换等与版本无关的运营投放不触发重新解析（产品决策：背景严格跟随游戏版本）
            if (IsCacheFreshFor(meta, request.Region, cacheDir, requireVersionMatch: true, gameVersion))
            {
                return ResolvedFromCache(meta!, cacheDir)!;
            }

            return await ResolveCoreAsync(request, resolver, cacheDir, meta, gameVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>仅读取磁盘缓存（启动预加载路径）：命中返回缓存（区域需一致），绝不触发网络；未命中返回 null。</summary>
    public Task<ResolvedBackdrop?> ResolveCachedAsync(BackdropRequest request)
    {
        var cacheDir = Path.Combine(_cacheRoot, Sanitize(request.GameId));
        var meta = ReadMeta(Path.Combine(cacheDir, "meta.json"));
        var hit = IsCacheFreshFor(meta, request.Region, cacheDir, requireVersionMatch: false)
            ? ResolvedFromCache(meta!, cacheDir)
            : null;
        return Task.FromResult(hit);
    }

    /// <summary>读取磁盘缓存元数据里记录的游戏版本（无缓存或旧格式元数据返回 null；纯读盘，不触发网络）。</summary>
    public string? GetCachedGameVersion(string gameId)
    {
        var meta = ReadMeta(Path.Combine(_cacheRoot, Sanitize(gameId), "meta.json"));
        return string.IsNullOrEmpty(meta?.GameVersion) ? null : meta.GameVersion;
    }

    /// <summary>缓存是否可直接使用：区域一致、背景文件在；requireVersionMatch 时还要求缓存记录的游戏版本与传入版本一致。</summary>
    private static bool IsCacheFreshFor(
        BackdropMeta? meta, string region, string cacheDir, bool requireVersionMatch, string? gameVersion = null) =>
        meta is { } cached
        && string.Equals(cached.Region, region, StringComparison.Ordinal)
        && (!requireVersionMatch
            || (gameVersion is not null && string.Equals(cached.GameVersion, gameVersion, StringComparison.Ordinal)))
        && File.Exists(Path.Combine(cacheDir, cached.File));

    /// <summary>从缓存元数据构建解析结果；背景文件缺失返回 null（海报缺失回退海报直链，交由加载服务处理）。</summary>
    private static ResolvedBackdrop? ResolvedFromCache(BackdropMeta meta, string cacheDir)
    {
        if (!File.Exists(Path.Combine(cacheDir, meta.File)))
        {
            return null;
        }

        return new ResolvedBackdrop(
            Path.Combine(cacheDir, meta.File),
            meta.Kind,
            meta.PosterFile is { } poster && File.Exists(Path.Combine(cacheDir, poster))
                ? Path.Combine(cacheDir, poster)
                : meta.PosterUrl);
    }

    private async Task<ResolvedBackdrop?> ResolveCoreAsync(
        BackdropRequest request, IBackdropResolver resolver, string cacheDir,
        BackdropMeta? meta, string? gameVersion, CancellationToken cancellationToken)
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

        // 远程确认成功且地址/类型未变：仅升级元数据里的区域与版本（不重下），
        // 让下次启动的版本门控能够命中
        if (remote is not null
            && meta is { } cached
            && string.Equals(cached.Url, remote.Url, StringComparison.OrdinalIgnoreCase)
            && cached.Kind == remote.Kind)
        {
            WriteMeta(Path.Combine(cacheDir, "meta.json"),
                cached with { Region = request.Region, GameVersion = gameVersion });
        }

        // 地址或类型变化（版本更新换投放）→ 重新下载覆盖缓存
        if (remote is not null
            && (meta is null
                || !string.Equals(meta.Url, remote.Url, StringComparison.OrdinalIgnoreCase)
                || meta.Kind != remote.Kind))
        {
            var downloaded = await TryDownloadAsync(remote, cacheDir, request.Region, gameVersion, cancellationToken)
                .ConfigureAwait(false);
            if (downloaded is { } download)
            {
                WriteMeta(Path.Combine(cacheDir, "meta.json"), download.Meta);
                return download.Resolved(cacheDir);
            }
        }

        // 缓存命中（远程一致，或远程不可用时的兜底）
        if (meta is { } cachedHit && ResolvedFromCache(cachedHit, cacheDir) is { } hit)
        {
            return hit;
        }

        // 无缓存：有直链就交直链（调用方的加载服务支持 http），否则放弃
        return remote is null ? null : new ResolvedBackdrop(remote.Url, remote.Kind, remote.PosterUrl);
    }

    /// <summary>下载背景本体（视频用流式写盘）与可选的首帧海报；返回缓存元数据或 null。</summary>
    private async Task<(BackdropMeta Meta, Func<string, ResolvedBackdrop> Resolved)?> TryDownloadAsync(
        BackdropSource source, string cacheDir, string region, string? gameVersion, CancellationToken cancellationToken)
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

            // 海报与本体同源投放（官方配置里配套给出）：海报失败不拖垮视频缓存，仅退回海报直链
            string? posterFile = null;
            if (source.PosterUrl is not null)
            {
                try
                {
                    posterFile = await DownloadToFileAsync(
                        source.PosterUrl, cacheDir, "poster", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when ((ex is HttpRequestException or IOException or InvalidOperationException
                    or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
                {
                    logger?.LogInformation("Backdrop poster download failed: {Message}", ex.Message);
                }
            }

            return (
                new BackdropMeta(source.Url, backdropFile, source.Kind, source.PosterUrl, posterFile, region, gameVersion),
                cacheDir => new ResolvedBackdrop(
                    Path.Combine(cacheDir, backdropFile),
                    source.Kind,
                    posterFile is null ? source.PosterUrl : Path.Combine(cacheDir, posterFile)));
        }
        catch (Exception ex) when ((ex is HttpRequestException or IOException or InvalidOperationException
            or FormatException or NotSupportedException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            // FormatException/NotSupportedException：畸形或非 http(s) 的 url 在构造请求时抛出，
            // 同样走"本次失败回退缓存/主题渐变"，不能穿透服务层
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
        // 官方配置给出的地址逆向协议不可信：畸形 URL 走默认扩展名，交由下载失败回退兜底
        var ext = Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? Path.GetExtension(parsed.AbsolutePath) is { Length: > 1 } e ? e : ".img"
            : ".img";
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

        // Windows 语义：旧背景仍被上会话播放器占用时 Delete/Move 会失败（Linux rename 总能成功）；
        // 删不掉就换时间戳备用名落盘，meta 记录新名，版本刷新不因占用而整体失败（旧文件残留少量可接受）。
        // 注意 File.Exists 对目录返回 false，占位可能是异常残留的目录，两者都要查
        var fileName = $"{baseName}{ext}";
        var finalPath = Path.Combine(cacheDir, fileName);
        FileUtilities.DeleteQuiet(finalPath);
        if (File.Exists(finalPath) || Directory.Exists(finalPath))
        {
            fileName = $"{baseName}-{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
            finalPath = Path.Combine(cacheDir, fileName);
        }

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

    private static string Sanitize(string value) => string.Concat(value.Where(char.IsLetterOrDigit));

    /// <summary>背景缓存元数据：来源直链 + 本地文件名（+ 类型与海报），用于判断"是否有更新"；
    /// 区域与游戏版本驱动版本门控（区域/版本均未变化时跳过远程解析直接用缓存）。</summary>
    /// <param name="Url">来源直链。</param>
    /// <param name="File">背景本体缓存文件名（backdrop&lt;ext&gt;）。</param>
    /// <param name="Kind">背景类型（图/视频）。</param>
    /// <param name="PosterUrl">海报来源直链（视频类型专用）。</param>
    /// <param name="PosterFile">海报缓存文件名（poster&lt;ext&gt;）。</param>
    /// <param name="Region">缓存对应的区域（cn/global；旧格式元数据为 null，首次重新解析后升级）。</param>
    /// <param name="GameVersion">缓存抓取时的游戏版本（渠道 LatestVersion；旧格式元数据为 null）。</param>
    private sealed record BackdropMeta(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("kind")] BackdropKind Kind = BackdropKind.Image,
        [property: JsonPropertyName("posterUrl")] string? PosterUrl = null,
        [property: JsonPropertyName("posterFile")] string? PosterFile = null,
        [property: JsonPropertyName("region")] string? Region = null,
        [property: JsonPropertyName("gameVersion")] string? GameVersion = null);
}
