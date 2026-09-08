using System.Text.Json;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Models;

public class GameCatalogJsonTests
{
    private const string FullJson = """
        {
          "settings": {
            "installRoot": "~/Games",
            "theme": "Dark",
            "maxParallelDownloads": 8,
            "language": "zh-CN"
          },
          "games": [
            {
              "id": "wuthering-waves",
              "displayName": "鸣潮",
              "icon": "icons/wuwa.png",
              "channel": "kuro",
              "installDir": "WutheringWaves",
              "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
              "launch": {
                "commandTemplate": "{exe}",
                "workingDirectory": "{installDir}",
                "environment": { "WINEPREFIX": "/home/user/.wine" }
              },
              "servers": [
                { "id": "cn", "name": "国服", "options": { "indexUrl": "https://example.com/cn/index.json" } },
                { "id": "global", "name": "国际服", "options": { "indexUrl": "https://example.com/global/index.json" } }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Deserialize_MinimalJson_AppliesDefaults()
    {
        var catalog = GameCatalogService.Parse("""{ "settings": { "installRoot": "~/Games" }, "games": [ { "id": "a", "displayName": "A", "channel": "kuro", "installDir": "A", "executable": "a.exe", "servers": [ { "id": "s1", "name": "S1" } ] } ] }""");

        Assert.Equal(ThemeMode.System, catalog.Settings.Theme);
        Assert.Equal("{exe}", catalog.Games[0].Launch.CommandTemplate);
        Assert.Equal("{installDir}", catalog.Games[0].Launch.WorkingDirectory);
    }

    [Fact]
    public void Deserialize_FullJson_ParsesAllFields()
    {
        var catalog = GameCatalogService.Parse(FullJson);

        var settings = catalog.Settings;
        Assert.Equal("~/Games", settings.InstallRoot);
        Assert.Equal(ThemeMode.Dark, settings.Theme);
        Assert.Equal("zh-CN", settings.Language);

        var game = Assert.Single(catalog.Games);
        Assert.Equal("wuthering-waves", game.Id);
        Assert.Equal("鸣潮", game.DisplayName);
        Assert.Equal("icons/wuwa.png", game.Icon);
        Assert.Equal("kuro", game.Channel);
        Assert.Equal("WutheringWaves", game.InstallDir);
        Assert.Equal("Client/Binaries/Win64/Client-Win64-Shipping.exe", game.Executable);
        Assert.Equal("/home/user/.wine", game.Launch.Environment["WINEPREFIX"]);

        Assert.Equal(2, game.Servers.Count);
        Assert.Equal("国服", game.Servers[0].Name);
        Assert.Equal("https://example.com/global/index.json", game.Servers[1].Options["indexUrl"]);
    }

    [Fact]
    public void Deserialize_AcceptsPascalCase()
    {
        var catalog = GameCatalogService.Parse("""{ "settings": { "installRoot": "~/Games" }, "Games": [ { "Id": "a", "DisplayName": "A", "Channel": "kuro", "InstallDir": "A", "Executable": "a.exe", "Servers": [ { "Id": "s", "Name": "S" } ] } ] }""");

        Assert.Equal("a", catalog.Games[0].Id);
    }

    [Fact]
    public void Deserialize_IgnoresUnknownFields()
    {
        var json = """{ "settings": { "installRoot": "~/Games" }, "futureSetting": true, "games": [ { "id": "a", "displayName": "A", "channel": "kuro", "installDir": "A", "executable": "a.exe", "futureGameField": 1, "servers": [ { "id": "s", "name": "S" } ] } ] }""";

        var catalog = GameCatalogService.Parse(json);

        Assert.Single(catalog.Games);
    }

    [Fact]
    public void Deserialize_AcceptsCommentsAndTrailingCommas()
    {
        var json = """
        {
          // 用户可以写注释
          "settings": { "installRoot": "~/Games", },
          "games": [],
        }
        """;

        var catalog = GameCatalogService.Parse(json);

        Assert.Empty(catalog.Games);
        Assert.Equal("~/Games", catalog.Settings.InstallRoot);
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesValues()
    {
        var original = GameCatalogService.Parse(FullJson);

        var json = GameCatalogService.Serialize(original);
        var reparsed = GameCatalogService.Parse(json);

        var game = Assert.Single(reparsed.Games);
        Assert.Equal("wuthering-waves", game.Id);
        Assert.Equal(ThemeMode.Dark, reparsed.Settings.Theme);
        Assert.Equal(2, game.Servers.Count);
    }

    [Fact]
    public void Serialize_UsesCamelCase()
    {
        var catalog = new GameCatalog
        {
            Settings = new AppSettings { InstallRoot = "~/Games" },
            Games = [ValidGame()],
        };

        var json = GameCatalogService.Serialize(catalog);

        Assert.Contains("\"installRoot\"", json);
        Assert.Contains("\"displayName\"", json);
        Assert.Contains("\"theme\"", json);
    }

    [Fact]
    public void Deserialize_MalformedJson_ThrowsValidationException()
    {
        Assert.Throws<GameCatalogValidationException>(() => GameCatalogService.Parse("{ not json "));
    }

    [Fact]
    public void Deserialize_InvalidEnumValue_ThrowsValidationException()
    {
        var json = """{ "settings": { "theme": "Neon" }, "games": [] }""";

        var ex = Assert.Throws<GameCatalogValidationException>(() => GameCatalogService.Parse(json));

        Assert.NotEmpty(ex.Errors);
    }

    private static GameDefinition ValidGame() => new()
    {
        Id = "test-game",
        DisplayName = "测试游戏",
        Channel = "kuro",
        InstallDir = "TestGame",
        Executable = "game.exe",
        Servers = [new GameServer { Id = "s1", Name = "服务器1" }],
    };
}
