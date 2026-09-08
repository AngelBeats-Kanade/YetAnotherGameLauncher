using System.Text;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Channels.Hypergryph;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Channels.Hypergryph.Tests;

/// <summary>
/// 终末地背景解析（官方启动器 get_main_bg_image）：请求打到 web batch_proxy，
/// 端点参数来自 server options，语言按区域取值。
/// </summary>
public class EndfieldBackdropResolverTests
{
    private const string WebBatchProxyUrl = "https://launcher.hypergryph.com/api/proxy/web/batch_proxy";

    private readonly StubHttpHandler _handler = new();

    private EndfieldBackdropResolver CreateResolver() => new(new HttpClient(_handler));

    private static BackdropRequest Request(string region) => new(
        "arknights-endfield",
        "hypergryph",
        region,
        InstallDir: null,
        ServerOptions: new Dictionary<string, string>
        {
            ["apiBase"] = "https://launcher.hypergryph.com/api",
            ["appcode"] = "6LL0KJuqHBVz33WK",
            ["channel"] = "1",
            ["subChannel"] = "1",
        });

    [Fact]
    public async Task GetBackdrop_EmptyVideoUrl_ReturnsImageBackdrop()
    {
        _handler.Map(WebBatchProxyUrl, """
            {
              "proxy_rsps": [
                {
                  "kind": "get_main_bg_image",
                  "get_main_bg_image_rsp": {
                    "data_version": "",
                    "main_bg_image": {
                      "url": "https://hg-utils-public.hycdn.cn/hg-utils/prod/x/current.png",
                      "md5": "aa",
                      "video_url": ""
                    }
                  }
                }
              ]
            }
            """);

        var source = await CreateResolver().GetBackdropUrlAsync(Request("cn"));

        Assert.Equal("https://hg-utils-public.hycdn.cn/hg-utils/prod/x/current.png", source!.Url);
        Assert.Equal(BackdropKind.Image, source.Kind);
        Assert.Null(source.PosterUrl);
    }

    [Fact]
    public async Task GetBackdrop_VideoUrl_Present_ReturnsVideoBackdropWithImagePoster()
    {
        _handler.Map(WebBatchProxyUrl, """
            {
              "proxy_rsps": [
                {
                  "kind": "get_main_bg_image",
                  "get_main_bg_image_rsp": {
                    "main_bg_image": {
                      "url": "https://cdn.example.com/current.png",
                      "video_url": "https://cdn.example.com/current.mp4"
                    }
                  }
                }
              ]
            }
            """);

        var source = await CreateResolver().GetBackdropUrlAsync(Request("cn"));

        Assert.Equal(BackdropKind.Video, source!.Kind);
        Assert.Equal("https://cdn.example.com/current.mp4", source.Url);
        Assert.Equal("https://cdn.example.com/current.png", source.PosterUrl);
    }

    [Fact]
    public async Task GetBackdrop_ChineseRegion_RequestsZhCnLanguage()
    {
        _handler.Map(WebBatchProxyUrl, """
            { "proxy_rsps": [ { "kind": "get_main_bg_image", "get_main_bg_image_rsp": { "main_bg_image": { "url": "https://cdn.example.com/cn.png" } } } ] }
            """);

        await CreateResolver().GetBackdropUrlAsync(Request("cn"));

        var body = await ReadRequestBody(WebBatchProxyUrl);
        Assert.Contains("\"language\":\"zh-cn\"", body);
        Assert.Contains("\"appcode\":\"6LL0KJuqHBVz33WK\"", body);
        Assert.Contains("\"source\":\"launcher\"", body);
    }

    [Fact]
    public async Task GetBackdrop_GlobalRegion_RequestsEnUsLanguage()
    {
        _handler.Map("https://launcher.gryphline.com/api/proxy/web/batch_proxy", """
            { "proxy_rsps": [ { "kind": "get_main_bg_image", "get_main_bg_image_rsp": { "main_bg_image": { "url": "https://cdn.example.com/os.png" } } } ] }
            """);
        var request = Request("global") with
        {
            ServerOptions = new Dictionary<string, string>
            {
                ["apiBase"] = "https://launcher.gryphline.com/api",
            },
        };

        var source = await CreateResolver().GetBackdropUrlAsync(request);

        // 缺省参数回退国际服实测值（与版本接口同源）
        Assert.Equal("https://cdn.example.com/os.png", source!.Url);
        var body = await ReadRequestBody("https://launcher.gryphline.com/api/proxy/web/batch_proxy");
        Assert.Contains("\"language\":\"en-us\"", body);
        Assert.Contains("\"appcode\":\"YDUTE5gscDZ229CW\"", body);
    }

    [Fact]
    public async Task GetBackdrop_MissingApiBase_ReturnsNull()
    {
        var request = Request("cn") with
        {
            ServerOptions = new Dictionary<string, string>(),
        };

        var url = await CreateResolver().GetBackdropUrlAsync(request);

        Assert.Null(url);
        Assert.Empty(_handler.Requests);
    }

    private async Task<string> ReadRequestBody(string expectedUrl)
    {
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(expectedUrl, request.RequestUri!.ToString());
        using var reader = new StreamReader(request.Content!.ReadAsStream(), Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
