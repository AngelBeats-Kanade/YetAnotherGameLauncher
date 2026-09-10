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
        public required GameCatalogService CatalogService { get; init; }
        public required FakeBackdropResolver KuroBackdrop { get; init; }
        public required FakeBackdropResolver GryphlineBackdrop { get; init; }
        public StubHttpHandler BackgroundHandler { get; init; } = new();
        public required UmuLauncherInstaller UmuInstaller { get; init; }

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

        /// <summary>循环淡化层（测试桩恒为 null）。</summary>
        public IImage? FadeFrame => null;

        /// <summary>循环淡化不透明度（测试桩恒为 0）。</summary>
        public double FadeOpacity => 0;

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
    /// autostart 可注入真实 AutostartService（回归测试用），缺省为 FakeProcessRunner 版本；
    /// platformInfo 缺省为 Windows 假平台——让全部测试在任意 OS 上确定性地走 Windows 语义
    /// （真实 LinuxPlatformInfo 会扫描真机 Proton 与 /proc NVIDIA，导致结果随测试机状态漂移）；
    /// linuxProtonVersions 供 Linux 首运推荐模板逻辑使用（null = 现场扫描）；
    /// linuxUmuPath/linuxWinePath 同理，缺省为空串 = 声明"未安装"（禁用真机 PATH 扫描）；
    /// linuxDataHome 缺省落临时目录，保证 games.json 里的 prefix 路径确定性。
    /// </summary>
    public static Context Build(
        string? configJson = SampleConfigJson,
        Func<string?>? templateFactory = null,
        IAutostartService? autostart = null,
        IFilePickerService? filePicker = null,
        IVideoBackdropPlayer? videoPlayer = null,
        IPlatformInfo? platformInfo = null,
        IReadOnlyList<string>? linuxProtonVersions = null,
        string? linuxUmuPath = "",
        string? linuxWinePath = "",
        string? linuxDataHome = null)
    {
        var tempDir = new TempDir();
        var configPath = tempDir.FilePath("games.json");
        var gamesRoot = tempDir.FilePath("games-root").Replace(System.IO.Path.DirectorySeparatorChar, '/');
        if (configJson is not null)
        {
            // 安装根目录必须落在临时目录内，避免测试间状态泄漏
            var json = configJson.Replace("~/yagl-test-games", gamesRoot);
            File.WriteAllText(configPath, json);
        }

        // 模板同样要重写安装根目录：首运物化默认配置时不能把根目录指向真实家目录
        if (templateFactory is not null)
        {
            var userTemplate = templateFactory;
            templateFactory = () => userTemplate()?.Replace("~/yagl-test-games", gamesRoot);
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

        var catalogService = new GameCatalogService(configPath);
        // pathValue 空串 = 禁用真机 PATH 扫描（wine/umu 预检确定性失败），日志落临时目录
        var launcherService = new GameLauncherService(
            new FakeProcessRunner(),
            logDirectory: tempDir.FilePath("logs"),
            pathValue: "");
        var umuInstaller = new UmuLauncherInstaller(
            new HttpClient(backgroundHandler), httpDownloader);
        var vm = new MainWindowViewModel(
            catalogService,
            new GameUpdateService(downloader, new FakePatchApplier()),
            launcherService,
            httpDownloader,
            autostart ?? new WindowsAutostartService(new FakeProcessRunner()),
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
            videoPlayer,
            platformInfo: platformInfo ?? new FakePlatformInfo(isLinux: false),
            linuxProtonVersions: linuxProtonVersions,
            linuxUmuPath: linuxUmuPath,
            linuxWinePath: linuxWinePath,
            linuxDataHome: linuxDataHome ?? tempDir.FilePath("data-home"),
            umuInstaller: umuInstaller);

        return new Context
        {
            Vm = vm,
            TempDir = tempDir,
            CatalogService = catalogService,
            Kuro = kuro,
            Gryphline = gryphline,
            Downloader = downloader,
            ConfigPath = configPath,
            KuroBackdrop = kuroBackdrop,
            GryphlineBackdrop = gryphlineBackdrop,
            BackgroundHandler = backgroundHandler,
            UmuInstaller = umuInstaller,
        };
    }
}
