using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>
/// 库洛官方启动器背景配置直连客户端：
/// 3.8+ 从资源索引（games.json 的 resourcePackIndexUrl）的 <c>config.functionCode.background</c>
/// 取当期背景哈希，再取与索引同目录的 <c>background/{hash}/{lang}.json</c>；旧 launcher-config
/// （根级 <c>functionCode.background</c>）两跳用于未配置资源索引的服务器（国际服/旧配置），
/// 或资源索引成功拉取但未声明背景时的回退。
/// 背景内容 JSON 含 backgroundFile 背景视频 / firstFrameImage 首帧图 / slogan 横幅的 CDN 直链。
/// 历史端点 switch.json 已停止投放背景（实测仅剩功能开关），官方改用哈希寻址后内容轮换天然免 CDN 缓存污染。
/// 约束：国际服 CDN 无条件返回 gzip 响应体，注入的 HttpClient 必须开启 AutomaticDecompression
/// （应用共享 handler 已开启，见 NetworkProxyManager）。
/// </summary>
public sealed class KuroSwitchConfigClient(HttpClient httpClient, ILogger<KuroSwitchConfigClient>? logger = null)
{
    /// <summary>背景配置对时效不敏感：单请求 8 秒超时 + 一次瞬态重试，拿不到即返回 null（上层回退上次缓存/主题渐变），不阻塞启动链路。</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 从版本接口地址推导 launcher-config 地址。官方 CDN 的目录约定（注意两段是双 launcher 前缀）：
    /// 版本接口 {host}/launcher/game/{gameId}/{appKey}/index.json ↔ 配置 {host}/launcher/launcher/{appKey}/{gameId}/index.json。
    /// 形态不符（用户自定义了非常规 indexUrl）返回 null。
    /// </summary>
    public static string? DeriveLauncherConfigUrl(string? indexUrl) =>
        ParseGameIndexUrl(indexUrl) is { } parts
            ? $"{parts.Scheme}://{parts.Authority}/launcher/launcher/{parts.AppKey}/{parts.GameId}/index.json"
            : null;

    /// <summary>
    /// 从版本接口地址 + 背景投放哈希推导背景内容地址：
    /// {host}/launcher/{appKey}/{gameId}/background/{backgroundHash}/{lang}.json。哈希或语言缺失返回 null。
    /// </summary>
    public static string? DeriveBackgroundUrl(string? indexUrl, string? backgroundHash, string lang)
    {
        if (string.IsNullOrWhiteSpace(backgroundHash) || string.IsNullOrWhiteSpace(lang))
        {
            return null;
        }

        return ParseGameIndexUrl(indexUrl) is { } parts
            ? $"{parts.Scheme}://{parts.Authority}/launcher/{parts.AppKey}/{parts.GameId}/background/" +
                $"{Uri.EscapeDataString(backgroundHash)}/{Uri.EscapeDataString(lang)}.json"
            : null;
    }

    /// <summary>
    /// 从游戏资源索引地址 + 背景哈希推导 3.8+ 背景内容地址（相对索引所在目录）：
    /// <c>{索引目录}/background/{hash}/{lang}.json</c>。哈希/语言缺失或地址非法返回 null。
    /// </summary>
    public static string? DeriveGameConfigBackgroundUrl(
        string? gameConfigUrl, string? backgroundHash, string lang)
    {
        if (string.IsNullOrWhiteSpace(gameConfigUrl)
            || !Uri.TryCreate(gameConfigUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(backgroundHash)
            || string.IsNullOrWhiteSpace(lang))
        {
            return null;
        }

        var lastSlash = gameConfigUrl.LastIndexOf('/');
        if (lastSlash < uri.GetLeftPart(UriPartial.Authority).Length)
        {
            return null;
        }

        var directory = gameConfigUrl[..(lastSlash + 1)];
        return $"{directory}background/{Uri.EscapeDataString(backgroundHash)}/{Uri.EscapeDataString(lang)}.json";
    }

    /// <summary>解析版本接口地址的约定段；形态不符返回 null。</summary>
    private static (string Scheme, string Authority, string GameId, string AppKey)? ParseGameIndexUrl(string? indexUrl)
    {
        if (string.IsNullOrWhiteSpace(indexUrl)
            || !Uri.TryCreate(indexUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["launcher", "game", _, _, "index.json"]
            ? (uri.Scheme, uri.Authority, segments[2], segments[3])
            : null;
    }

    /// <summary>
    /// 3.8+ 背景解析入口：优先从 <paramref name="gameConfigUrl"/>（资源索引 / bundle 索引）读取
    /// <c>config.functionCode.background</c>，再取同目录背景内容。仅当资源索引成功拉取但明确没有背景哈希时
    /// 才回退旧 launcher-config；资源索引请求失败或已声明哈希但内容取不到时返回 null，交上层保留旧缓存并重试。
    /// </summary>
    /// <param name="indexUrl">旧版本接口地址（用于回退推导 launcher-config）。</param>
    /// <param name="gameConfigUrl">3.8+ 资源索引完整地址（games.json 的 resourcePackIndexUrl）；null = 直接走旧链路。</param>
    /// <param name="region">背景区域（"cn"/"global"），决定首选语言。</param>
    /// <param name="cancellationToken">调用方取消令牌（用户退出时不等超时）。</param>
    public async Task<KuroSwitchConfig?> FetchAsync(
        string? indexUrl, string? gameConfigUrl, string region, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(gameConfigUrl))
        {
            var (config, canFallBackToLegacy) = await FetchFromGameConfigAsync(
                gameConfigUrl, region, cancellationToken).ConfigureAwait(false);
            if (config is not null)
            {
                return config;
            }

            if (!canFallBackToLegacy)
            {
                return null;
            }
        }

        return await FetchFromLauncherConfigAsync(indexUrl, region, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 直连拉取旧 launcher-config 两跳背景配置；未投放（配置无背景哈希 / 内容无 backgroundFile / 功能关闭）
    /// 或网络失败返回 null。保留给 3.8 之前的资源索引 / 国际服回退。
    /// </summary>
    /// <param name="indexUrl">games.json 该服务器的旧版本接口地址（用于推导 launcher-config）。</param>
    /// <param name="region">背景区域（"cn"/"global"），决定首选语言。</param>
    /// <param name="cancellationToken">调用方取消令牌（用户退出时不等超时）。</param>
    public Task<KuroSwitchConfig?> FetchAsync(
        string? indexUrl, string region, CancellationToken cancellationToken = default) =>
        FetchFromLauncherConfigAsync(indexUrl, region, cancellationToken);

    /// <summary>
    /// 从 3.8+ 资源索引取背景配置。返回 <c>CanFallBackToLegacy=true</c> 仅表示索引成功拉取但未声明背景
    /// （旧服务形态）；索引不可用或已声明哈希但内容失败都返回 false，避免旧投放被写成当前缓存。
    /// </summary>
    private async Task<(KuroSwitchConfig? Config, bool CanFallBackToLegacy)> FetchFromGameConfigAsync(
        string gameConfigUrl, string region, CancellationToken cancellationToken)
    {
        var (backgroundHash, configAvailable) = await FetchBackgroundHashWithRetryAsync(
            gameConfigUrl, "Kuro game config", cancellationToken).ConfigureAwait(false);
        if (!configAvailable)
        {
            return (null, false);
        }

        if (backgroundHash is null)
        {
            return (null, true);
        }

        var config = await FetchBackgroundContentAsync(
            lang => DeriveGameConfigBackgroundUrl(gameConfigUrl, backgroundHash, lang),
            region,
            cancellationToken).ConfigureAwait(false);
        return (config, false);
    }

    /// <summary>旧 launcher-config 两跳：哈希未投放/网络失败返回 null；内容失败同样返回 null 交上层回退缓存。</summary>
    private async Task<KuroSwitchConfig?> FetchFromLauncherConfigAsync(
        string? indexUrl, string region, CancellationToken cancellationToken)
    {
        var configUrl = DeriveLauncherConfigUrl(indexUrl);
        if (configUrl is null)
        {
            return null;
        }

        var (backgroundHash, configAvailable) = await FetchBackgroundHashWithRetryAsync(
            configUrl, "Kuro launcher config", cancellationToken).ConfigureAwait(false);
        if (!configAvailable || backgroundHash is null)
        {
            return null;
        }

        return await FetchBackgroundContentAsync(
            lang => DeriveBackgroundUrl(indexUrl, backgroundHash, lang),
            region,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>背景内容的语言候选链：主语言（区域映射）优先，en 与 zh-Hans 兜底（去重保序）。</summary>
    internal static IEnumerable<string> LanguageCandidates(string region)
    {
        var primary = region.Equals("cn", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : "en";
        return new[] { primary, "en", "zh-Hans" }.Distinct(StringComparer.Ordinal);
    }

    /// <summary>按语言候选链拉取背景内容；404/无背景字段换下一个语言，瞬态失败每语言重试一次后整体放弃。</summary>
    private async Task<KuroSwitchConfig?> FetchBackgroundContentAsync(
        Func<string, string?> urlForLanguage, string region, CancellationToken cancellationToken)
    {
        foreach (var lang in LanguageCandidates(region))
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var backgroundUrl = urlForLanguage(lang);
                    if (backgroundUrl is null)
                    {
                        break;
                    }

                    var config = await FetchBackgroundAsync(backgroundUrl, cancellationToken).ConfigureAwait(false);
                    if (config is not null)
                    {
                        return config;
                    }

                    // 该语言未投放（404 或内容无背景字段/功能关闭）：换下一个语言
                    break;
                }
                catch (Exception ex) when (IsTransient(ex, cancellationToken))
                {
                    if (attempt >= 1)
                    {
                        logger?.LogDebug("Kuro background config fetch failed ({Lang}): {Message}", lang, ex.Message);
                        return null;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>拉取索引并取出背景哈希（带一次瞬态重试）；Available=false 表示网络/解析失败，null 哈希表示明确未投放。</summary>
    private async Task<(string? Hash, bool Available)> FetchBackgroundHashWithRetryAsync(
        string configUrl, string logLabel, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return (await FetchBackgroundHashAsync(configUrl, cancellationToken).ConfigureAwait(false), true);
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                if (attempt >= 1)
                {
                    logger?.LogDebug("{Label} fetch failed: {Message}", logLabel, ex.Message);
                    return (null, false);
                }
            }
        }
    }

    /// <summary>拉取索引（带缓存破坏时间戳）并读取根级或 config 下的 functionCode.background。</summary>
    private async Task<string?> FetchBackgroundHashAsync(string configUrl, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);
        using var response = await httpClient.GetAsync(
            AppendCacheBuster(configUrl), timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);

        // 逆向协议：3.8+ 在 config.functionCode，旧 launcher-config 在根级 functionCode；
        // 全程 TryGetProperty，不抛键缺失异常
        return TryReadBackgroundHash(doc.RootElement);
    }

    /// <summary>读取根级 functionCode.background，缺失时再读 config.functionCode.background。</summary>
    private static string? TryReadBackgroundHash(JsonElement root)
    {
        var rootHash = ReadBackgroundHash(root);
        if (rootHash is not null)
        {
            return rootHash;
        }

        return root.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object
            ? ReadBackgroundHash(config)
            : null;
    }

    /// <summary>读取单个对象元素下的 functionCode.background；不是非空字符串返回 null。</summary>
    private static string? ReadBackgroundHash(JsonElement element)
    {
        if (!element.TryGetProperty("functionCode", out var functionCode)
            || functionCode.ValueKind != JsonValueKind.Object
            || !functionCode.TryGetProperty("background", out var background)
            || background.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var hash = background.GetString();
        return string.IsNullOrWhiteSpace(hash) ? null : hash;
    }

    /// <summary>拉取指定 URL 的背景内容；404（该语言未投放）或无背景字段返回 null，其他失败抛异常由调用方重试。</summary>
    private async Task<KuroSwitchConfig?> FetchBackgroundAsync(
        string backgroundUrl, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);
        using var response = await httpClient.GetAsync(
            AppendCacheBuster(backgroundUrl), timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // 404 是确定性的语言缺失：不重试、不触发 EnsureSuccessStatusCode 的异常路径
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ParseAsync(response, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>给请求 URL 追加缓存破坏时间戳（已有 query 时用 &amp; 连接）。</summary>
    private static string AppendCacheBuster(string url) =>
        $"{url}{(url.Contains('?') ? '&' : '?')}_t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

    /// <summary>解析背景配置 JSON：委托给 KuroSwitchConfig.FromJson，无投放（无 backgroundFile 或功能关闭）返回 null。</summary>
    private static async Task<KuroSwitchConfig?> ParseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return KuroSwitchConfig.FromJson(doc.RootElement);
    }

    /// <summary>判定可重试的瞬态失败（网络/解析/超时/畸形地址 FormatException）；用户主动取消不重试。</summary>
    internal static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or JsonException or TaskCanceledException
            or InvalidOperationException or FormatException
        && !cancellationToken.IsCancellationRequested;
}
