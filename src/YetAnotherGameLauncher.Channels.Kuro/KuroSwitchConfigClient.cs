using System.Text.Json;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>
/// 库洛官方启动器的运营配置（switch.json）直连客户端：里面承载背景视频/首帧图/横幅的 CDN 直链，
/// 由官方运营阶段性投放（随版本/活动更新）。请求地址可从 games.json 现有的 indexUrl 推导，
/// 每次启动直连即可与官方启动器同步拿到最新投放。
/// </summary>
public sealed class KuroSwitchConfigClient(HttpClient httpClient, ILogger<KuroSwitchConfigClient>? logger = null)
{
    /// <summary>背景配置对时效不敏感：8 秒超时 + 一次瞬态重试，拿不到就走本地缓存扫描兜底，不阻塞启动链路。</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 从版本接口地址推导启动器配置地址。官方 CDN 的目录约定：
    /// 版本接口 {host}/launcher/game/{gameId}/{hash}/index.json ↔ 配置 {host}/launcher/{hash}/{gameId}/switch.json。
    /// 形态不符（用户自定义了非常规 indexUrl）返回 null。
    /// </summary>
    public static string? DeriveSwitchUrl(string? indexUrl)
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
                ? $"{uri.Scheme}://{uri.Authority}/launcher/{segments[3]}/{segments[2]}/switch.json"
                : null;
    }

    /// <summary>直连拉取并解析配置；无背景投放（官方配置无 backgroundFile）或网络失败返回 null。
    /// 连接重置/超时这类瞬态失败重试一次（代理刚就绪、DNS 抖动等场景），最多两请求。</summary>
    /// <param name="indexUrl">games.json 该服务器的版本接口地址（用于推导配置地址）。</param>
    /// <param name="cancellationToken">调用方取消令牌（用户退出时不等超时）。</param>
    public async Task<KuroSwitchConfig?> FetchAsync(string? indexUrl, CancellationToken cancellationToken = default)
    {
        var switchUrl = DeriveSwitchUrl(indexUrl);
        if (switchUrl is null)
        {
            return null;
        }

        for (var attempt = 0; ; attempt++)
        {
            // _t 为缓存破坏时间戳，与官方启动器请求形态一致，保证拿到最新投放（每次尝试重新生成）
            var url = $"{switchUrl}?_t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(FetchTimeout);
                using var response = await httpClient.GetAsync(url, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await ParseAsync(response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is HttpRequestException or JsonException or TaskCanceledException
                or InvalidOperationException) && !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= 1)
                {
                    logger?.LogDebug("Kuro switch.json fetch failed: {Message}", ex.Message);
                    return null;
                }
            }
        }
    }

    // CA2007 误报：await using 声明的 DisposeAsync 续体由编译器生成，无法对其追加 ConfigureAwait。
#pragma warning disable CA2007
    /// <summary>解析配置 JSON：委托给 KuroSwitchConfig.FromJson，无 backgroundFile（官方未投放背景）返回 null。</summary>
    private static async Task<KuroSwitchConfig?> ParseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return KuroSwitchConfig.FromJson(doc.RootElement);
    }
}
