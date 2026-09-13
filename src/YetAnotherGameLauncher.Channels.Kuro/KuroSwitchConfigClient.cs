using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>
/// 库洛官方启动器运营配置直连客户端，两跳取当期背景投放（2026-09 实测链路）：
/// ① launcher-config（functionCode.background 字段）给出当期背景投放哈希；
/// ② 背景内容 JSON（backgroundFile 背景视频 / firstFrameImage 首帧图 / slogan 横幅的 CDN 直链）。
/// 两级地址均可由 games.json 现有的 indexUrl 推导，每次启动直连即可与官方启动器同步拿到最新投放。
/// 历史端点 switch.json 已停止投放背景（实测仅剩功能开关），官方改用哈希寻址后内容轮换天然免 CDN 缓存污染。
/// 约束：国际服 CDN 无条件返回 gzip 响应体，注入的 HttpClient 必须开启 AutomaticDecompression
/// （应用共享 handler 已开启，见 NetworkProxyManager）。
/// </summary>
public sealed class KuroSwitchConfigClient(HttpClient httpClient, ILogger<KuroSwitchConfigClient>? logger = null)
{
    /// <summary>背景配置对时效不敏感：单请求 8 秒超时 + 一次瞬态重试，拿不到就走本地缓存扫描兜底，不阻塞启动链路。</summary>
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
        return segments.Length == 5
            && segments[0] == "launcher"
            && segments[1] == "game"
            && segments[4] == "index.json"
            ? (uri.Scheme, uri.Authority, segments[2], segments[3])
            : null;
    }

    /// <summary>
    /// 直连拉取当期背景配置；未投放（配置无背景哈希 / 内容无 backgroundFile / 功能关闭）或网络失败返回 null。
    /// 背景内容按语言投放：主语言（区域映射）优先，404 时回退其他语言（实测国服仅 zh-Hans，国际服多语言）；
    /// 连接重置/超时这类瞬态失败每跳重试一次，重试仍失败即整体放弃——网络层面的失败换语言也一样失败。
    /// </summary>
    /// <param name="indexUrl">games.json 该服务器的版本接口地址（用于推导两级配置地址）。</param>
    /// <param name="region">背景区域（"cn"/"global"），决定首选语言。</param>
    /// <param name="cancellationToken">调用方取消令牌（用户退出时不等超时）。</param>
    public async Task<KuroSwitchConfig?> FetchAsync(
        string? indexUrl, string region, CancellationToken cancellationToken = default)
    {
        var configUrl = DeriveLauncherConfigUrl(indexUrl);
        if (configUrl is null)
        {
            return null;
        }

        // 第一跳：launcher-config 取背景投放哈希
        string? backgroundHash;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                backgroundHash = await FetchBackgroundHashAsync(configUrl, cancellationToken).ConfigureAwait(false);
                if (backgroundHash is not null)
                {
                    break;
                }

                // 配置里没有背景投放：确定性结果，不重试不换语言
                return null;
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                if (attempt >= 1)
                {
                    logger?.LogDebug("Kuro launcher config fetch failed: {Message}", ex.Message);
                    return null;
                }
            }
        }

        // 第二跳：按语言候选链取背景内容
        foreach (var lang in LanguageCandidates(region))
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var config = await FetchBackgroundAsync(indexUrl!, backgroundHash, lang, cancellationToken)
                        .ConfigureAwait(false);
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

    /// <summary>背景内容的语言候选链：主语言（区域映射）优先，en 与 zh-Hans 兜底（去重保序）。</summary>
    internal static IEnumerable<string> LanguageCandidates(string region)
    {
        var primary = region.Equals("cn", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : "en";
        return new[] { primary, "en", "zh-Hans" }.Distinct(StringComparer.Ordinal);
    }

    /// <summary>拉取 launcher-config（带缓存破坏时间戳，与官方启动器请求形态一致）并取出 functionCode.background。</summary>
    private async Task<string?> FetchBackgroundHashAsync(string configUrl, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);
        using var response = await httpClient.GetAsync(
            $"{configUrl}?_t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // CA2007 误报：await using 声明的 DisposeAsync 续体由编译器生成，无法对其追加 ConfigureAwait。
#pragma warning disable CA2007
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
#pragma warning restore CA2007
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);

        // 逆向协议、结构可能随官方更新变化：全程 TryGetProperty，不抛键缺失异常
        return doc.RootElement.TryGetProperty("functionCode", out var functionCode)
            && functionCode.ValueKind == JsonValueKind.Object
            && functionCode.TryGetProperty("background", out var background)
            && background.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(background.GetString())
            ? background.GetString()
            : null;
    }

    /// <summary>拉取指定语言的背景内容；404（该语言未投放）或无背景字段返回 null，其他失败抛异常由调用方重试。</summary>
    private async Task<KuroSwitchConfig?> FetchBackgroundAsync(
        string indexUrl, string backgroundHash, string lang, CancellationToken cancellationToken)
    {
        var backgroundUrl = DeriveBackgroundUrl(indexUrl, backgroundHash, lang);
        if (backgroundUrl is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);
        using var response = await httpClient.GetAsync(
            $"{backgroundUrl}?_t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // 404 是确定性的语言缺失：不重试、不触发 EnsureSuccessStatusCode 的异常路径
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ParseAsync(response, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>解析背景配置 JSON：委托给 KuroSwitchConfig.FromJson，无投放（无 backgroundFile 或功能关闭）返回 null。</summary>
    private static async Task<KuroSwitchConfig?> ParseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // CA2007 误报：await using 声明的 DisposeAsync 续体由编译器生成，无法对其追加 ConfigureAwait。
#pragma warning disable CA2007
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return KuroSwitchConfig.FromJson(doc.RootElement);
    }

    /// <summary>判定可重试的瞬态失败（网络/解析/超时）；用户主动取消不重试。</summary>
    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException
        && !cancellationToken.IsCancellationRequested;
}
