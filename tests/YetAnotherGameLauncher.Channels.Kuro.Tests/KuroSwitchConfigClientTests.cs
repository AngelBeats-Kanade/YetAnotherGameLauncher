using System.Text;
using YetAnotherGameLauncher.Channels.Kuro;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

/// <summary>
/// 库洛启动器运营配置（switch.json）客户端：地址推导、直连解析与失败静默。
/// </summary>
public class KuroSwitchConfigClientTests
{
    private readonly StubHttpHandler _handler = new();

    private KuroSwitchConfigClient CreateClient() => new(new HttpClient(_handler));

    [Theory]
    [InlineData(
        "https://prod-cn-alicdn-gamestarter.kurogame.com/launcher/game/G152/10003_H1/index.json",
        "https://prod-cn-alicdn-gamestarter.kurogame.com/launcher/10003_H1/G152/switch.json")]
    [InlineData(
        "https://prod-alicdn-gamestarter.kurogame.com/launcher/game/G153/50004_H2/index.json",
        "https://prod-alicdn-gamestarter.kurogame.com/launcher/50004_H2/G153/switch.json")]
    public void DeriveSwitchUrl_GameIndexUrl_TransformsSegmentOrder(string indexUrl, string expected)
    {
        Assert.Equal(expected, KuroSwitchConfigClient.DeriveSwitchUrl(indexUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://cdn.example.com/other/path/config.json")]
    [InlineData("https://cdn.example.com/launcher/G152/10003_H1/index.json")]
    public void DeriveSwitchUrl_UnexpectedShape_ReturnsNull(string? indexUrl)
    {
        Assert.Null(KuroSwitchConfigClient.DeriveSwitchUrl(indexUrl));
    }

    [Fact]
    public async Task FetchAsync_WithBackgroundFile_ParsesConfig()
    {
        _handler.Map("https://cdn.example.com/launcher/10003_H1/G152/switch.json", """
            {"functionSwitch":1,
             "backgroundFile":"https://cdn.example.com/bg.mp4",
             "backgroundFileType":2,
             "firstFrameImage":"https://cdn.example.com/first.webp",
             "slogan":"https://cdn.example.com/slogan.png"}
            """);

        var config = await CreateClient().FetchAsync(
            "https://cdn.example.com/launcher/game/G152/10003_H1/index.json");

        Assert.Equal("https://cdn.example.com/bg.mp4", config!.BackgroundFile);
        Assert.Equal("https://cdn.example.com/first.webp", config.FirstFrameImage);
        Assert.Equal("https://cdn.example.com/slogan.png", config.Slogan);
    }

    [Fact]
    public async Task FetchAsync_WithoutBackgroundFile_ReturnsNull()
    {
        // 官方阶段性投放：当前无背景字段（实测 2026-09 即为该形态）
        _handler.Map("https://cdn.example.com/launcher/10003_H1/G152/switch.json",
            """{"recharge":{"functionSwitch":1,"jump":"https://pay.example.com"},"needLogin":0}""");

        var config = await CreateClient().FetchAsync(
            "https://cdn.example.com/launcher/game/G152/10003_H1/index.json");

        Assert.Null(config);
    }

    [Fact]
    public async Task FetchAsync_NetworkFails_ReturnsNull()
    {
        // 未注册 URL → Stub 抛错：背景是装饰性资源，网络失败必须静默回退
        var config = await CreateClient().FetchAsync(
            "https://cdn.example.com/launcher/game/G152/10003_H1/index.json");

        Assert.Null(config);
    }

    [Fact]
    public async Task FetchAsync_TransientFailure_RetriesOnceAndSucceeds()
    {
        _handler.Map("https://cdn.example.com/launcher/10003_H1/G152/switch.json", """
            {"backgroundFile":"https://cdn.example.com/bg.mp4"}
            """);
        _handler.FailFirstN = 1; // 第一次瞬态故障（连接重置类）

        var config = await CreateClient().FetchAsync(
            "https://cdn.example.com/launcher/game/G152/10003_H1/index.json");

        Assert.Equal("https://cdn.example.com/bg.mp4", config!.BackgroundFile);
        Assert.Equal(2, _handler.Requests.Count); // 恰好重试一次
    }

    [Fact]
    public async Task FetchAsync_PersistentFailure_GivesUpAfterRetry()
    {
        _handler.FailFirstN = 10;

        var config = await CreateClient().FetchAsync(
            "https://cdn.example.com/launcher/game/G152/10003_H1/index.json");

        Assert.Null(config);
        Assert.Equal(2, _handler.Requests.Count); // 不无限重试：最多两次
    }
}
