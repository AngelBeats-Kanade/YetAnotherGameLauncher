using System.Net.Http.Json;
using System.Text.Json;
using YetAnotherGameLauncher.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Channels.Hypergryph;

/// <summary>
/// 明日方舟：终末地的详情页背景解析：官方启动器的 get_main_bg_image 接口（与版本接口同一 batch_proxy，
/// web 前缀），返回当期主背景（官方启动器首页同源，随版本更新）。响应同时携带静态图 url 与背景视频
/// video_url（官方同款动态背景），视频优先、静态图兜底。
/// 端点参数来自 games.json 对应 server 的 options（apiBase/appcode/channel/subChannel），
/// 语言按区域取值：cn → zh-cn，global → en-us。协议无官方文档，字段来自社区逆向，可能随官方更新变化。
/// </summary>
public sealed class EndfieldBackdropResolver(HttpClient httpClient, ILogger<EndfieldBackdropResolver>? logger = null) : IBackdropResolver
{
    public async Task<BackdropSource?> GetBackdropUrlAsync(BackdropRequest request, CancellationToken cancellationToken = default)
    {
        var apiBase = GryphlineProtocol.ApiBaseOrNull(request.ServerOptions);
        if (apiBase is null)
        {
            return null;
        }

        var appcode = GryphlineProtocol.OptionOrDefault(request.ServerOptions, GryphlineChannelApi.AppcodeOptionKey, GryphlineChannelApi.DefaultGameAppcode);
        var channel = GryphlineProtocol.OptionOrDefault(request.ServerOptions, GryphlineChannelApi.ChannelOptionKey, GryphlineChannelApi.DefaultChannelId);
        var subChannel = GryphlineProtocol.OptionOrDefault(request.ServerOptions, GryphlineChannelApi.SubChannelOptionKey, GryphlineChannelApi.DefaultSubChannelId);
        var language = request.Region == "cn" ? "zh-cn" : "en-us";

        var payload = new
        {
            proxy_reqs = new[]
            {
                new
                {
                    kind = "get_main_bg_image",
                    get_main_bg_image_req = new
                    {
                        appcode,
                        channel,
                        sub_channel = subChannel,
                        language,
                        platform = "Windows",
                        source = "launcher",
                    },
                },
            },
        };

        logger?.LogDebug("Fetching Endfield main bg image ({Region})", request.Region);

        using var response = await httpClient.PostAsJsonAsync(
            $"{apiBase.TrimEnd('/')}/proxy/web/batch_proxy", payload, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // CA2007 误报：await using 声明的 DisposeAsync 续体由编译器生成，无法对其追加 ConfigureAwait。
#pragma warning disable CA2007
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        // 协议为逆向所得、结构可能随官方更新变化：全程用 TryGetProperty/数组检查，不抛键缺失异常
        if (!doc.RootElement.TryGetProperty("proxy_rsps", out var rsps)
            || rsps.ValueKind != JsonValueKind.Array
            || rsps.GetArrayLength() == 0
            || !rsps[0].TryGetProperty("get_main_bg_image_rsp", out var rsp)
            || !rsp.TryGetProperty("main_bg_image", out var bg)
            || !bg.TryGetProperty("url", out var urlElement)
            || urlElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        // 视频优先（官方动态背景）；video_url 缺省/为空时回退静态图（历史行为）
        var imageUrl = urlElement.GetString();
        var videoUrl = bg.TryGetProperty("video_url", out var videoElement)
            && videoElement.ValueKind == JsonValueKind.String
            ? videoElement.GetString()
            : null;

        return string.IsNullOrWhiteSpace(videoUrl)
            ? BackdropSource.ImageOrNullIfEmpty(imageUrl)
            : new BackdropSource(videoUrl, BackdropKind.Video, imageUrl);
    }
}
