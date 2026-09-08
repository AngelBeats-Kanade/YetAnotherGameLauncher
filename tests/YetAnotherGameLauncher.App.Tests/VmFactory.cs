using Avalonia.Media;
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
              "nameLocalized": { "zh-CN": "鸣潮", "en-US": "Wuthering Waves" },
              "channel": "kuro",
              "installDir": "WutheringWaves",
              "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
              "servers": [ { "id": "cn", "name": "国服" } ]
            },
            {
              "id": "arknights-endfield",
              "displayName": "明日方舟：终末地",
              "nameLocalized": { "zh-CN": "明日方舟：终末地", "en-US": "Arknights: Endfield" },
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
        public required FakeBackdropResolver KuroBackdrop { get; init; }
        public required FakeBackdropResolver GryphlineBackdrop { get; init; }
        public StubHttpHandler BackgroundHandler { get; init; } = new();

        public void Dispose() => TempDir.Dispose();
    }

    /// <summary>可编程的背景解析器假实现：默认返回 null（回退主题渐变）。</summary>
    public sealed class FakeBackdropResolver : IBackdropResolver
    {
        /// <summary>按区域返回背景来源；null = 解析失败。</summary>
        public Func<string, BackdropSource?>? Resolver { get; set; }

        public Task<BackdropSource?> GetBackdropUrlAsync(BackdropRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Resolver?.Invoke(request.Region));
    }

    /// <summary>可编程的视频播放器假实现：记录起播/停止调用，帧事件由测试手动触发。</summary>
    public sealed class FakeVideoPlayer : IVideoBackdropPlayer
    {
        /// <summary>起播是否成功（默认 true）。</summary>
        public Func<string, bool>? PlayHandler { get; set; }

        public List<string> PlayedPaths { get; } = [];

        public int StopCount { get; private set; }

        /// <summary>帧位图（测试可注入假帧）。</summary>
        public IImage? Frame { get; set; }

        public event EventHandler? FrameUpdated;

        public Task<bool> PlayAsync(string videoPath, CancellationToken cancellationToken = default)
        {
            PlayedPaths.Add(videoPath);
            return Task.FromResult(PlayHandler?.Invoke(videoPath) ?? true);
        }

        public void Stop() => StopCount++;

        /// <summary>模拟解码器产出帧（测试手动驱动，UI 线程触发）。</summary>
        public void RaiseFrame() => FrameUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 构建 ViewModel。configJson 为 null 时<b>不创建</b>配置文件（模拟首次运行）；
    /// templateFactory 对应注入 VM 的默认配置模板工厂（null = 无模板）；
    /// autostart 可注入真实 AutostartService（回归测试用），缺省为 FakeProcessRunner 版本。
    /// </summary>
    public static Context Build(
        string? configJson = SampleConfigJson,
        Func<string?>? templateFactory = null,
        IAutostartService? autostart = null,
        IFilePickerService? filePicker = null,
        IVideoBackdropPlayer? videoPlayer = null)
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
        var kuroBackdrop = new FakeBackdropResolver();
        var gryphlineBackdrop = new FakeBackdropResolver();
        var backdropService = new GameBackdropService(
            new HttpClient(backgroundHandler),
            new Dictionary<string, IBackdropResolver>
            {
                ["kuro"] = kuroBackdrop,
                ["hypergryph"] = gryphlineBackdrop,
            },
            cacheRoot: tempDir.FilePath("backdrops"));

        var vm = new MainWindowViewModel(
            new GameCatalogService(configPath),
            new GameUpdateService(downloader, new FakePatchApplier()),
            new GameLauncherService(new FakeProcessRunner()),
            httpDownloader,
            autostart ?? new AutostartService(new FakeProcessRunner()),
            new ThemeService(),
            new LocalizationService(),
            new BackgroundImageService(new HttpClient(backgroundHandler)),
            backdropService,
            channelKey => channelKey switch
            {
                "kuro" => kuro,
                "hypergryph" => gryphline,
                _ => null,
            },
            templateFactory,
            filePicker,
            videoPlayer);

        return new Context
        {
            Vm = vm,
            TempDir = tempDir,
            Kuro = kuro,
            Gryphline = gryphline,
            Downloader = downloader,
            ConfigPath = configPath,
            KuroBackdrop = kuroBackdrop,
            GryphlineBackdrop = gryphlineBackdrop,
            BackgroundHandler = backgroundHandler,
        };
    }
}
