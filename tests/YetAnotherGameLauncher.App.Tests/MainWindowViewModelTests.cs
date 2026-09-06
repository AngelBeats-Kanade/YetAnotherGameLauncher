using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>MainWindowViewModel 状态机测试（纯 VM，无 UI）。</summary>
[Collection("sequential")]
public class MainWindowViewModelTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public MainWindowViewModelTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task Initialize_LoadsTwoGamesAndSelectsFirst()
    {
        await _ctx.Vm.InitializeAsync();

        Assert.Equal(2, _ctx.Vm.Games.Count);
        Assert.Equal("鸣潮", _ctx.Vm.SelectedGame?.DisplayName);
        Assert.Same(_ctx.Vm.SelectedGame, _ctx.Vm.CurrentPage);
    }

    [Fact]
    public async Task Initialize_ThemeFromConfig_IsApplied()
    {
        // 示例配置 theme=Dark：应反映到 ViewModel 的主题选择上
        await _ctx.Vm.InitializeAsync();

        Assert.Equal(Core.Models.ThemeMode.Dark, _ctx.Vm.SelectedTheme?.Mode);
    }

    [Fact]
    public async Task Initialize_GameStatus_ReflectsChannelInfo()
    {
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "3.6.0",
            PredownloadAvailable = true,
            PredownloadVersion = "3.7.0",
        };

        await _ctx.Vm.InitializeAsync();

        var wuwa = _ctx.Vm.Games[0];
        Assert.Equal("尚未安装", wuwa.StatusText);
        Assert.False(wuwa.IsInstalled);
        Assert.False(wuwa.CanLaunch);
        Assert.False(wuwa.HasUpdate); // 未安装谈不上"更新"
        Assert.True(wuwa.PredownloadAvailable);
        Assert.Equal("安装游戏", wuwa.InstallButtonText);
    }

    [Fact]
    public async Task SelectingGame_SwitchesPageAndRefreshes()
    {
        await _ctx.Vm.InitializeAsync();

        _ctx.Vm.SelectedGame = _ctx.Vm.Games[1];
        await Task.Yield();

        Assert.Same(_ctx.Vm.Games[1], _ctx.Vm.CurrentPage);
        Assert.Contains("最新", _ctx.Vm.Games[1].VersionText);
    }

    [Fact]
    public async Task InstallOrUpdate_RunsUpdateThroughFakeChannel_SavesState()
    {
        var zipBytes = TestZip.Create(("Client/game.exe", "MZ"));
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.6.0" };
        _ctx.Kuro.Manifests["3.6.0"] = new GameManifest
        {
            Version = "3.6.0",
            Files = [new ManifestFile("Client/game.exe", zipBytes.Length, Hashing.Md5Hex(zipBytes), Url: "https://cdn/game.exe")],
        };
        _ctx.Downloader.Responses["https://cdn/game.exe"] = zipBytes;

        await _ctx.Vm.InitializeAsync();
        await _ctx.Vm.Games[0].InstallOrUpdateCommand.ExecuteAsync(null);

        var wuwa = _ctx.Vm.Games[0];
        Assert.True(wuwa.IsInstalled);
        Assert.Equal("完成： → 3.6.0", wuwa.StatusText);
        Assert.Contains("https://cdn/game.exe", _ctx.Downloader.Requests);
    }

    [Fact]
    public async Task Predownload_WhenServerOpensWindow_StagesAndShowsBadge()
    {
        _ctx.Gryphline.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "1.0.0",
            PredownloadAvailable = true,
            PredownloadVersion = "1.1.0",
        };
        var zipBytes = TestZip.Create(("bin/ef.exe", "MZ"));
        _ctx.Gryphline.PredownloadManifest = new GameManifest
        {
            Version = "1.1.0",
            EntriesAreArchives = true,
            Files = [new ManifestFile("patch-1.1.0.zip", zipBytes.Length, Hashing.Md5Hex(zipBytes), Url: "https://cdn/patch.zip")],
        };
        _ctx.Downloader.Responses["https://cdn/patch.zip"] = zipBytes;

        await _ctx.Vm.InitializeAsync();
        await _ctx.Vm.Games[1].PredownloadCommand.ExecuteAsync(null);

        var endfield = _ctx.Vm.Games[1];
        Assert.True(endfield.HasStagedPredownload);
        Assert.False(endfield.PredownloadAvailable); // 已暂存，"预下载"按钮隐藏
        Assert.Contains("预下载完成", endfield.StatusText);
    }

    [Fact]
    public async Task LaunchCommand_WithoutInstall_DoesNothing()
    {
        await _ctx.Vm.InitializeAsync();

        await _ctx.Vm.Games[0].LaunchCommand.ExecuteAsync(null);

        Assert.False(_ctx.Vm.Games[0].CanLaunch);
        Assert.NotEqual("游戏已启动", _ctx.Vm.Games[0].StatusText);
    }

    [Fact]
    public async Task UnknownChannel_SkippedWithMessage()
    {
        using var ctx = VmFactory.Build("""{ "settings": { "installRoot": "~/g" }, "games": [ { "id": "x", "displayName": "未知", "channel": "nope", "installDir": "X", "executable": "x.exe", "servers": [ { "id": "s", "name": "S" } ] } ] }""");

        await ctx.Vm.InitializeAsync();

        Assert.Empty(ctx.Vm.Games);
        Assert.Contains("未注册的渠道", ctx.Vm.StatusMessage);
    }

    // ---------- 首次运行：自动生成默认配置文件 ----------

    [Fact]
    public async Task Initialize_MissingConfigFile_WithTemplate_GeneratesDefaultAndLoadsGames()
    {
        using var ctx = VmFactory.Build(configJson: null, templateFactory: () => VmFactory.SampleConfigJson);
        Assert.False(File.Exists(ctx.ConfigPath)); // 前置：确实没有配置文件

        await ctx.Vm.InitializeAsync();

        Assert.True(File.Exists(ctx.ConfigPath));
        var reloader = new GameCatalogService(ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.NotNull(reloader.Catalog);
        Assert.Equal(2, reloader.Catalog.Games.Count); // 模板内容（鸣潮 + 终末地）
        Assert.Equal(2, ctx.Vm.Games.Count);
        Assert.NotNull(ctx.Vm.SelectedGame);
        Assert.False(ctx.Vm.ConfigError);
        Assert.Contains("已生成默认配置文件", ctx.Vm.StatusMessage);
        Assert.True(ctx.Vm.ShowStatusAsHint); // 以"提示"而非"错误"样式展示
    }

    [Fact]
    public async Task Initialize_MissingConfigFile_WithoutTemplate_GeneratesMinimalDefault()
    {
        using var ctx = VmFactory.Build(configJson: null);

        await ctx.Vm.InitializeAsync();

        Assert.True(File.Exists(ctx.ConfigPath));
        var reloader = new GameCatalogService(ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.NotNull(reloader.Catalog);
        Assert.Empty(reloader.Catalog.Games); // 无模板时生成最小合法配置
        Assert.False(ctx.Vm.ConfigError);
    }

    [Fact]
    public async Task Initialize_SecondRun_DoesNotRegenerate()
    {
        using var ctx = VmFactory.Build(configJson: null, templateFactory: () => VmFactory.SampleConfigJson);
        await ctx.Vm.InitializeAsync();
        Assert.Contains("已生成默认配置文件", ctx.Vm.StatusMessage);
        var contentAfterFirstRun = await File.ReadAllTextAsync(ctx.ConfigPath);

        // 第二次初始化（模拟第二次启动）：文件已存在，不覆盖、不再提示"已生成"
        await ctx.Vm.InitializeAsync();

        Assert.DoesNotContain("已生成默认配置文件", ctx.Vm.StatusMessage);
        Assert.Equal(contentAfterFirstRun, await File.ReadAllTextAsync(ctx.ConfigPath));
    }

    // ---------- i18n：语言设置 ----------

    [Fact]
    public async Task Initialize_LanguageFromConfig_IsApplied()
    {
        using var ctx = VmFactory.Build("""
            {
              "settings": { "installRoot": "~/yagl-test-games", "theme": "Dark", "language": "en-US" },
              "games": [
                {
                  "id": "wuthering-waves", "displayName": "鸣潮", "channel": "kuro",
                  "installDir": "WutheringWaves", "executable": "Client/game.exe",
                  "servers": [ { "id": "cn", "name": "国服" } ]
                }
              ]
            }
            """);

        await ctx.Vm.InitializeAsync();

        Assert.Equal("en-US", ctx.Vm.Loc.Language);
        Assert.Equal("Install Game", ctx.Vm.Games[0].InstallButtonText);
        Assert.Equal("Not installed", ctx.Vm.Games[0].StatusText);
        Assert.Contains("1 games", ctx.Vm.GameCountText);
        // 主题选项显示名跟随语言重建
        Assert.Equal("Follow System", ctx.Vm.ThemeModes[0].DisplayName);
    }

    [Fact]
    public async Task SaveLanguage_WritesBackToConfigFile()
    {
        await _ctx.Vm.InitializeAsync();

        _ctx.Vm.Loc.SetLanguage("en-US");
        await _ctx.Vm.SaveLanguageAsync("en-US");

        var reloader = new GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.Equal("en-US", reloader.Catalog!.Settings.Language);
    }

    [Fact]
    public async Task LanguageSwitch_RefreshesExistingGameTexts()
    {
        await _ctx.Vm.InitializeAsync();
        Assert.Equal("安装游戏", _ctx.Vm.Games[0].InstallButtonText);

        _ctx.Vm.Loc.SetLanguage("en-US");
        await _ctx.Vm.Games[0].RefreshAsync(); // 语言切换会触发各游戏自动刷新，这里显式等待完成

        Assert.Equal("Install Game", _ctx.Vm.Games[0].InstallButtonText);
        Assert.Equal("Not installed", _ctx.Vm.Games[0].StatusText);
    }
}

// ---------- 配置迁移与安装根目录 ----------

public class ConfigMigrationTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public ConfigMigrationTests() => _ctx = VmFactory.Build("""
        {
          "settings": { "installRoot": "~/yagl-test-games", "theme": "Dark", "maxParallelDownloads": 4 },
          "games": [
            {
              "id": "wuthering-waves", "displayName": "鸣潮", "channel": "kuro",
              "installDir": "WutheringWaves", "executable": "Client/game.exe",
              "servers": [ { "id": "cn", "name": "国服" } ]
            }
          ]
        }
        """, templateFactory: () => SampleTemplateWithAllServers);

    public void Dispose() => _ctx.TempDir.Dispose();

    /// <summary>模拟新版内置模板：鸣潮含 3 个服务器与本地化名称。</summary>
    private static readonly string SampleTemplateWithAllServers = """
        {
          "settings": { "installRoot": "~/Games", "theme": "System", "language": "system", "schemaVersion": 3 },
          "games": [
            {
              "id": "wuthering-waves", "displayName": "鸣潮", "channel": "kuro",
              "nameLocalized": { "zh-CN": "鸣潮", "en-US": "Wuthering Waves" },
              "installDir": "WutheringWaves", "executable": "Client/game.exe",
              "servers": [
                { "id": "cn", "name": "国服" },
                { "id": "global", "name": "国际服" },
                { "id": "bilibili", "name": "B服" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task Initialize_OldSchema_MergesSampleServersAndLocalizedNames()
    {
        // 旧配置（schemaVersion 缺失）只有鸣潮国服；样例模板含国服/B服/国际服与本地化名称
        await _ctx.Vm.InitializeAsync();

        var wuwa = _ctx.Vm.Games[0];
        var serverIds = wuwa.Servers.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Superset(new HashSet<string> { "cn", "global", "bilibili" }, serverIds);
        Assert.False(string.IsNullOrWhiteSpace(wuwa.Game.NameLocalized["en-US"])); // 名称映射随迁移补齐
        Assert.Contains("补充", _ctx.Vm.StatusMessage);

        // schemaVersion 写回，二次启动不重复迁移
        var before = await File.ReadAllTextAsync(_ctx.ConfigPath);
        await _ctx.Vm.InitializeAsync();
        var after = await File.ReadAllTextAsync(_ctx.ConfigPath);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task UpdateInstallRoot_PersistsAndRebuildsGames()
    {
        await _ctx.Vm.InitializeAsync();

        var newRoot = _ctx.TempDir.FilePath("new-root").Replace(Path.DirectorySeparatorChar, '/');
        var ok = await _ctx.Vm.UpdateInstallRootAsync(newRoot);

        Assert.True(ok);
        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.Equal(newRoot, reloader.Catalog!.Settings.InstallRoot);
        // 游戏列表以新根目录重新解析
        Assert.StartsWith(newRoot, _ctx.Vm.Games[0].InstallDirPath.Replace((char)92, '/'));
    }

    [Fact]
    public async Task UpdateInstallRoot_Empty_ReturnsFalse()
    {
        await _ctx.Vm.InitializeAsync();

        Assert.False(await _ctx.Vm.UpdateInstallRootAsync("   "));
    }
}
