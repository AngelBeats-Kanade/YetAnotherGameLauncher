using YetAnotherGameLauncher.AppTests;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>游戏显示名 i18n：配置 nameLocalized 按当前语言取值，缺失回退 displayName。</summary>
[Collection("sequential")]
public class GameDisplayNameTests
{
    private const string ConfigJson = """
        {
          "settings": { "installRoot": "~/yagl-test-games", "theme": "Dark", "language": "zh-CN" },
          "games": [
            {
              "id": "wuthering-waves",
              "displayName": "鸣潮",
              "nameLocalized": { "zh-CN": "鸣潮", "en-US": "Wuthering Waves" },
              "channel": "kuro",
              "installDir": "WutheringWaves",
              "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
              "servers": [ { "id": "cn", "name": "CN" } ]
            },
            {
              "id": "no-map-game",
              "displayName": "Fallback Name",
              "channel": "kuro",
              "installDir": "NoMap",
              "executable": "game.exe",
              "servers": [ { "id": "cn", "name": "CN" } ]
            }
          ]
        }
        """;

    [Theory]
    [InlineData("zh-CN", "鸣潮")]
    [InlineData("en-US", "Wuthering Waves")]
    public async Task DisplayName_UsesLocalizedMapForCurrentLanguage(string language, string expected)
    {
        using var ctx = VmFactory.Build(configJson: ConfigJson);
        await ctx.Vm.InitializeAsync();
        ctx.Vm.Loc.SetLanguage(language);

        Assert.Equal(expected, ctx.Vm.Games[0].DisplayName);
        Assert.Equal(expected[..1], ctx.Vm.Games[0].IconText);
    }

    [Fact]
    public async Task DisplayName_MissingMap_FallsBackToDisplayName()
    {
        using var ctx = VmFactory.Build(configJson: ConfigJson);
        await ctx.Vm.InitializeAsync();
        ctx.Vm.Loc.SetLanguage("en-US");

        Assert.Equal("Fallback Name", ctx.Vm.Games[1].DisplayName);
    }

    [Fact]
    public async Task Migration_FillsLocalizedNamesFromSample()
    {
        // 旧配置（无 schemaVersion/nameLocalized）：迁移应从样例模板补齐名称映射
        const string legacyJson = """
            {
              "settings": { "installRoot": "~/yagl-test-games", "theme": "Dark" },
              "games": [
                {
                  "id": "wuthering-waves",
                  "displayName": "鸣潮",
                  "channel": "kuro",
                  "installDir": "WutheringWaves",
                  "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
                  "servers": [ { "id": "cn", "name": "CN" } ]
                }
              ]
            }
            """;

        using var ctx = VmFactory.Build(configJson: legacyJson, templateFactory: () => VmFactory.SampleConfigJson);
        await ctx.Vm.InitializeAsync();

        var game = ctx.Vm.Games[0];
        Assert.Equal("鸣潮", game.Game.NameLocalized["zh-CN"]);
        Assert.Equal("Wuthering Waves", game.Game.NameLocalized["en-US"]);
    }
}
