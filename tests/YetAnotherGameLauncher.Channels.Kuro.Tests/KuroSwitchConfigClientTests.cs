using YetAnotherGameLauncher.Channels.Kuro;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

/// <summary>
/// 库洛启动器运营配置直连客户端：两级地址推导（launcher-config / 背景内容）、两跳解析、
/// 语言回退与失败静默。新链路 2026-09 实测：switch.json 已停止投放背景，背景内容改由
/// launcher-config 的 functionCode.background 哈希寻址。
/// </summary>
public class KuroSwitchConfigClientTests
{
    private const string CnIndexUrl = "https://cdn.example.com/launcher/game/G152/10003_H1/index.json";
    private const string CnLauncherConfigUrl = "https://cdn.example.com/launcher/launcher/10003_H1/G152/index.json";
    private const string GlobalIndexUrl = "https://cdn.example.com/launcher/game/G153/50004_H2/index.json";

    private readonly StubHttpHandler _handler = new();

    private KuroSwitchConfigClient CreateClient() => new(new HttpClient(_handler));

    /// <summary>铺好两跳桩：launcher-config 返回背景哈希，指定语言的背景内容返回完整投放。</summary>
    private void MapTwoHop(string lang, string backgroundJson)
    {
        _handler.Map(CnLauncherConfigUrl, """{"functionCode":{"background":"HASH123"},"crashInitSwitch":0}""");
        _handler.Map($"https://cdn.example.com/launcher/10003_H1/G152/background/HASH123/{lang}.json", backgroundJson);
    }

    private const string FullBackgroundJson = """
        {"functionSwitch":1,
         "backgroundFile":"https://cdn.example.com/bg.mp4",
         "backgroundFileType":2,
         "firstFrameImage":"https://cdn.example.com/first.webp",
         "slogan":"https://cdn.example.com/slogan.png"}
        """;

    [Theory]
    [InlineData(
        CnIndexUrl,
        "https://cdn.example.com/launcher/launcher/10003_H1/G152/index.json")]
    [InlineData(
        GlobalIndexUrl,
        "https://cdn.example.com/launcher/launcher/50004_H2/G153/index.json")]
    public void DeriveLauncherConfigUrl_GameIndexUrl_TransformsSegmentOrder(string indexUrl, string expected)
    {
        Assert.Equal(expected, KuroSwitchConfigClient.DeriveLauncherConfigUrl(indexUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://cdn.example.com/other/path/config.json")]
    [InlineData("https://cdn.example.com/launcher/G152/10003_H1/index.json")]
    [InlineData("https://cdn.example.com/launcher/game/G152/10003_H1/extra/index.json")]
    public void DeriveLauncherConfigUrl_UnexpectedShape_ReturnsNull(string? indexUrl)
    {
        Assert.Null(KuroSwitchConfigClient.DeriveLauncherConfigUrl(indexUrl));
    }

    [Fact]
    public void DeriveBackgroundUrl_ComposesHashAndLanguage()
    {
        Assert.Equal(
            "https://cdn.example.com/launcher/10003_H1/G152/background/HASH123/zh-Hans.json",
            KuroSwitchConfigClient.DeriveBackgroundUrl(CnIndexUrl, "HASH123", "zh-Hans"));
    }

    [Theory]
    [InlineData(null, "zh-Hans")]
    [InlineData("", "zh-Hans")]
    [InlineData("HASH123", " ")]
    public void DeriveBackgroundUrl_MissingHashOrLanguage_ReturnsNull(string? hash, string lang)
    {
        Assert.Null(KuroSwitchConfigClient.DeriveBackgroundUrl(CnIndexUrl, hash, lang));
    }

    [Fact]
    public async Task FetchAsync_TwoHop_ParsesConfig()
    {
        MapTwoHop("zh-Hans", FullBackgroundJson);

        var config = await CreateClient().FetchAsync(CnIndexUrl, "cn");

        Assert.Equal("https://cdn.example.com/bg.mp4", config!.BackgroundFile);
        Assert.Equal("https://cdn.example.com/first.webp", config.FirstFrameImage);
        Assert.Equal("https://cdn.example.com/slogan.png", config.Slogan);
        Assert.Equal(1, config.FunctionSwitch);
        Assert.Equal(2, config.BackgroundFileType);
        Assert.True(config.IsVideo);
    }

    [Fact]
    public async Task FetchAsync_PrimaryLanguageMissing_FallsBackToNextLanguage()
    {
        // 国服实测：仅投放 zh-Hans，请求 en 是硬 404——这里反向构造（zh-Hans 缺、en 命中）验证回退推进
        MapTwoHop("en", FullBackgroundJson);

        var config = await CreateClient().FetchAsync(CnIndexUrl, "cn");

        Assert.Equal("https://cdn.example.com/bg.mp4", config!.BackgroundFile);
        // zh-Hans（404，不重试）→ en 命中：共三请求
        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task FetchAsync_GlobalRegion_PrefersEnglish()
    {
        _handler.Map(
            "https://cdn.example.com/launcher/launcher/50004_H2/G153/index.json",
            """{"functionCode":{"background":"HASH456"}}""");
        _handler.Map(
            "https://cdn.example.com/launcher/50004_H2/G153/background/HASH456/en.json",
            """{"backgroundFile":"https://cdn.example.com/global.mp4"}""");

        var config = await CreateClient().FetchAsync(GlobalIndexUrl, "global");

        Assert.Equal("https://cdn.example.com/global.mp4", config!.BackgroundFile);
        Assert.Equal(2, _handler.Requests.Count); // 主语言直接命中，不碰回退
    }

    [Fact]
    public async Task FetchAsync_NoBackgroundHash_ReturnsNullWithoutSecondHop()
    {
        // 官方阶段性未投放：launcher-config 无 functionCode.background
        _handler.Map(CnLauncherConfigUrl, """{"functionCode":{},"crashInitSwitch":0}""");

        Assert.Null(await CreateClient().FetchAsync(CnIndexUrl, "cn"));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task FetchAsync_BackgroundWithoutBackgroundFile_TriesAllLanguagesThenNull()
    {
        // 背景内容退化为功能开关（2026-09 实测 switch.json 的形态）：逐语言尝试后放弃
        MapTwoHop("zh-Hans", """{"recharge":{"functionSwitch":1},"needLogin":0}""");
        MapTwoHop("en", """{"recharge":{"functionSwitch":1},"needLogin":0}""");

        Assert.Null(await CreateClient().FetchAsync(CnIndexUrl, "cn"));
        Assert.Equal(3, _handler.Requests.Count); // launcher-config + zh-Hans + en（各一次，无重试）
    }

    [Fact]
    public async Task FetchAsync_FunctionSwitchOff_ReturnsNull()
    {
        MapTwoHop("zh-Hans", """{"functionSwitch":0,"backgroundFile":"https://cdn.example.com/bg.mp4"}""");
        MapTwoHop("en", """{"functionSwitch":0,"backgroundFile":"https://cdn.example.com/bg.mp4"}""");

        Assert.Null(await CreateClient().FetchAsync(CnIndexUrl, "cn"));
    }

    [Fact]
    public async Task FetchAsync_NetworkFails_ReturnsNull()
    {
        // 未注册 URL → 桩返回 404 → EnsureSuccessStatusCode 抛错：背景是装饰性资源，网络失败必须静默回退
        Assert.Null(await CreateClient().FetchAsync(CnIndexUrl, "cn"));
    }

    [Fact]
    public async Task FetchAsync_TransientFailure_RetriesOnceAndSucceeds()
    {
        MapTwoHop("zh-Hans", FullBackgroundJson);
        _handler.FailFirstN = 1; // 第一次瞬态故障（连接重置类）

        var config = await CreateClient().FetchAsync(CnIndexUrl, "cn");

        Assert.Equal("https://cdn.example.com/bg.mp4", config!.BackgroundFile);
        Assert.Equal(3, _handler.Requests.Count); // launcher-config 两次（重试）+ 背景内容一次
    }

    [Fact]
    public async Task FetchAsync_PersistentFailure_GivesUpAfterRetry()
    {
        _handler.FailFirstN = 10;

        Assert.Null(await CreateClient().FetchAsync(CnIndexUrl, "cn"));
        Assert.Equal(2, _handler.Requests.Count); // 不无限重试：第一跳最多两次
    }
}
