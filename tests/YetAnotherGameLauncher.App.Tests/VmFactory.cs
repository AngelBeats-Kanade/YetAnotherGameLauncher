using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.Themes;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>构建带两个假游戏（kuro + hypergryph 渠道）的 ViewModel，完全离线。</summary>
public static class VmFactory
{
    public const string SampleConfigJson = """
        {
          "settings": { "installRoot": "~/yagl-test-games", "theme": "Dark", "maxParallelDownloads": 4 },
          "games": [
            {
              "id": "wuthering-waves",
              "displayName": "鸣潮",
              "channel": "kuro",
              "installDir": "WutheringWaves",
              "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
              "servers": [ { "id": "cn", "name": "国服" } ]
            },
            {
              "id": "arknights-endfield",
              "displayName": "明日方舟：终末地",
              "channel": "hypergryph",
              "installDir": "ArknightsEndfield",
              "executable": "ArknightsEndfield/Binaries/Win64/ArknightsEndfield.exe",
              "servers": [ { "id": "global", "name": "国际服" } ]
            }
          ]
        }
        """;

    public sealed class Context : IDisposable
    {
        public required MainWindowViewModel Vm { get; init; }
        public required TempDir TempDir { get; init; }
        public required FakeChannel Kuro { get; init; }
        public required FakeChannel Gryphline { get; init; }
        public required FakeDownloader Downloader { get; init; }
        public required string ConfigPath { get; init; }
        public StubHttpHandler BackgroundHandler { get; init; } = new();

        public void Dispose() => TempDir.Dispose();
    }

    /// <summary>
    /// 构建 ViewModel。configJson 为 null 时<b>不创建</b>配置文件（模拟首次运行）；
    /// templateFactory 对应注入 VM 的默认配置模板工厂（null = 无模板）。
    /// </summary>
    public static Context Build(string? configJson = SampleConfigJson, Func<string?>? templateFactory = null)
    {
        var tempDir = new TempDir();
        var configPath = tempDir.FilePath("games.json");
        if (configJson is not null)
        {
            // 安装根目录必须落在临时目录内，避免测试间状态泄漏
            var json = configJson
                .Replace("~/yagl-test-games", tempDir.FilePath("games-root").Replace(System.IO.Path.DirectorySeparatorChar, '/'));
            File.WriteAllText(configPath, json);
        }

        var kuro = new FakeChannel();
        var gryphline = new FakeChannel();
        var downloader = new FakeDownloader();
        var backgroundHandler = new StubHttpHandler();
        var httpDownloader = new HttpFileDownloader(new HttpClient(backgroundHandler));

        var vm = new MainWindowViewModel(
            new GameCatalogService(configPath),
            new GameUpdateService(downloader, new FakePatchApplier()),
            new GameLauncherService(new FakeProcessRunner()),
            httpDownloader,
            new AutostartService(new FakeProcessRunner()),
            new ThemeService(),
            new LocalizationService(),
            new BackgroundImageService(new HttpClient(backgroundHandler)),
            channelKey => channelKey switch
            {
                "kuro" => kuro,
                "hypergryph" => gryphline,
                _ => null,
            },
            templateFactory);

        return new Context
        {
            Vm = vm, TempDir = tempDir, Kuro = kuro, Gryphline = gryphline,
            Downloader = downloader, ConfigPath = configPath,
            BackgroundHandler = backgroundHandler,
        };
    }
}
