using Avalonia.Media;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Channels.Kuro;
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
              "executable": "Endfield.exe",
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

        /// <summary>VM 实际持有的代理管理器（断言共享 handler 随保存切换用——组合根装配缺口回归）。</summary>
        public required NetworkProxyManager ProxyManager { get; init; }

        /// <summary>VM 实际持有的唤取服务（缓存目录在 TempDir/gacha-cache）。</summary>
        public required KuroGachaService GachaService { get; init; }

        /// <summary>测试注入的视频播放器工厂（镜像生产：每游戏一路播放器）。null = 游戏无播放器（海报兜底）。</summary>
        public required Func<YetAnotherGameLauncher.Services.IVideoBackdropPlayer>? PlayerFactory { get; init; }

        /// <summary>默认工厂创建的假播放器（与 Games 同序；测试未注入自定义工厂时用 Players[i] 观察第 i 个游戏）。</summary>
        public required IReadOnlyList<FakeVideoPlayer> Players { get; init; }

        public void Dispose() => TempDir.Dispose();
    }

    /// <summary>可编程的背景解析器假实现：默认返回 null（回退主题渐变）。</summary>
    public sealed class FakeBackdropResolver : IBackdropResolver
    {
        /// <summary>按区域返回背景来源；null = 解析失败。</summary>
        public Func<string, BackdropSource?>? Resolver { get; set; }

        /// <summary>按完整请求返回背景来源（需要断言 ServerOptions 等字段时用）；优先于 Resolver。</summary>
        public Func<BackdropRequest, BackdropSource?>? RequestResolver { get; set; }

        /// <summary>异步形态（优先于 RequestResolver）：返回挂起 Task 可把资产加载钉在真实
        /// await 点上——代际门/竞态测试构造确定性交错的注入缝。</summary>
        public Func<BackdropRequest, Task<BackdropSource?>>? AsyncRequestResolver { get; set; }

        /// <summary>解析器被调用次数（验证"版本一致时跳过远程解析"用）。</summary>
        public int ResolveCount { get; private set; }

        public Task<BackdropSource?> GetBackdropUrlAsync(BackdropRequest request, CancellationToken cancellationToken = default)
        {
            ResolveCount++;
            return AsyncRequestResolver is { } async
                ? async(request)
                : Task.FromResult(RequestResolver?.Invoke(request) ?? Resolver?.Invoke(request.Region));
        }
    }

    /// <summary>可编程的视频播放器假实现：记录起播/停止/暂停/恢复调用，帧事件由测试手动触发。</summary>
    public sealed class FakeVideoPlayer : IVideoBackdropPlayer
    {
        /// <summary>起播是否成功（默认 true）。</summary>
        public Func<string, bool>? PlayHandler { get; set; }

        /// <summary>异步起播（优先于 PlayHandler）：返回挂起的 Task 可模拟起播窗口内被后发起播抢先。</summary>
        public Func<string, Task<bool>>? AsyncPlayHandler { get; set; }

        public List<string> PlayedPaths { get; } = [];

        public int StopCount { get; private set; }

        /// <summary>Pause 调用次数（切非游戏页保活断言用）。</summary>
        public int PauseCount { get; private set; }

        /// <summary>Resume 调用次数（重进详情页续播断言用）。</summary>
        public int ResumeCount { get; private set; }

        /// <summary>是否有活动会话：PlayAsync 起播置位、Stop/起播失败清零（与 FfmpegVideoBackdropPlayer 一致）。
        /// 假实现的 PlayAsync 即刻完成，会话"存活"直至 Stop——足以驱动 VM 侧续播/重启分叉；
        /// 被后发起播抢先的过期调用失败返回时不得清掉新会话（与真实现的代际守卫一致）。</summary>
        public bool IsSessionActive { get; private set; }

        /// <summary>起播代际（与真实现同语义）：过期起播的失败收尾无权清会话标志。</summary>
        private int _playGeneration;

        /// <summary>帧位图（测试可注入假帧）。</summary>
        public IImage? Frame { get; set; }

        /// <summary>循环淡化层（测试桩恒为 null）。</summary>
        public IImage? FadeFrame => null;

        /// <summary>循环淡化不透明度（测试桩恒为 0）。</summary>
        public double FadeOpacity => 0;

        public event EventHandler? FrameUpdated;

        public async Task<bool> PlayAsync(string videoPath, CancellationToken cancellationToken = default)
        {
            PlayedPaths.Add(videoPath);
            var generation = ++_playGeneration;
            IsSessionActive = true;
            var playing = AsyncPlayHandler is { } async
                ? await async(videoPath)
                : PlayHandler?.Invoke(videoPath) ?? true;
            if (!playing && generation == _playGeneration)
            {
                IsSessionActive = false;
            }

            return playing;
        }

        public void Stop()
        {
            StopCount++;
            _playGeneration++; // 在途起播即时作废：其失败收尾不得复活会话标志
            IsSessionActive = false;
            // 契约：Stop 清空帧缓冲（与 FfmpegVideoBackdropPlayer 一致）
            Frame = null;
        }

        /// <summary>契约：暂停不清帧、不断会话（与 FfmpegVideoBackdropPlayer 一致）。</summary>
        public void Pause() => PauseCount++;

        /// <summary>恢复泊车的会话；无会话时为 no-op（与 FfmpegVideoBackdropPlayer 一致）。</summary>
        public void Resume() => ResumeCount++;

        /// <summary>模拟解码器产出帧（测试手动驱动，UI 线程触发）。</summary>
        public void RaiseFrame() => FrameUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 构建 ViewModel。configJson 为 null 时<b>不创建</b>配置文件（模拟首次运行）；
    /// playerFactory 为每个游戏创建独立的背景视频播放器（镜像生产"播放器按游戏独占"），
    /// 缺省每游戏一个 FakeVideoPlayer（记录进 Context.Players）；
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
        Func<IVideoBackdropPlayer>? playerFactory = null,
        IPlatformInfo? platformInfo = null,
        IReadOnlyList<string>? linuxProtonVersions = null,
        string? linuxWinePath = "",
        string? linuxDataHome = null,
        YetAnotherGameLauncher.Core.Services.Umu.NativeUmuLauncher? nativeUmu = null,
        YetAnotherGameLauncher.Core.Abstractions.IUmuComponentProvisioner? umuProvisioner = null,
        NetworkProxyManager? proxyManager = null,
        KuroGachaService? gachaService = null)
    {
        var tempDir = new TempDir();
        // 生产默认 500ms 的视频起播延迟会拖慢/打乱既有断言时序——测试统一置零
        // （涉视频用例均在 sequential 集合，静态开关无并行竞态；专门用例自行临时调回）
        GameItemViewModel.VideoStartDeferral = TimeSpan.Zero;
        // 播放器按游戏独占（镜像生产 transient）：缺省每游戏一路 FakeVideoPlayer 并按序记录
        var players = new List<FakeVideoPlayer>();
        Func<IVideoBackdropPlayer> resolvedPlayerFactory = playerFactory!;
        if (playerFactory is null)
        {
            resolvedPlayerFactory = () =>
            {
                var player = new FakeVideoPlayer();
                players.Add(player);
                return player;
            };
        }
        var configPath = tempDir.FilePath("games.json");
        var gamesRoot = tempDir.FilePath("games-root").Replace(Path.DirectorySeparatorChar, '/');
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
        // 生产组合根恒传 NetworkProxyManager（App.axaml.cs）；测试镜像生产装配，缺省新建
        var resolvedProxyManager = proxyManager ?? new NetworkProxyManager();
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
        // pathValue 空串 = 禁用真机 PATH 扫描（wine 预检确定性失败），日志落临时目录
        var launcherService = new GameLauncherService(
            new FakeProcessRunner(),
            logDirectory: tempDir.FilePath("logs"),
            pathValue: "");
        // 生产组合根第 14 参为 KuroGachaService；测试镜像曾缺省（null），ShowGacha 门与
        // 语言切换重建唤取页分支因此全测试不可达（2026-09-20 复审修复）
        var gacha = gachaService ?? new KuroGachaService(
            new HttpClient(backgroundHandler), cacheDirectory: tempDir.FilePath("gacha-cache"));
        var vm = new MainWindowViewModel(
            catalogService,
            new GameUpdateService(downloader, new FakePatchApplier()),
            launcherService,
            httpDownloader,
            autostart ?? new WindowsAutostartService(new FakeProcessRunner()),
            new ThemeService(),
            new LocalizationService(),
            // 磁盘缓存根目录落临时目录：VM 测试的 http 图标不得写进真实用户配置目录
            new BackgroundImageService(new HttpClient(backgroundHandler), diskCacheRoot: tempDir.FilePath("image-cache")),
            backdropService,
            channelKey => channelKey switch
            {
                "kuro" => kuro,
                "hypergryph" => gryphline,
                _ => null,
            },
            templateFactory,
            filePicker,
            resolvedPlayerFactory,
            gacha,
            proxyManager: resolvedProxyManager,
            platformInfo: platformInfo ?? new FakePlatformInfo(isLinux: false),
            linuxProtonVersions: linuxProtonVersions,
            linuxWinePath: linuxWinePath,
            linuxDataHome: linuxDataHome ?? tempDir.FilePath("data-home"),
            nativeUmu: nativeUmu,
            umuProvisioner: umuProvisioner);

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
            ProxyManager = resolvedProxyManager,
            GachaService = gacha,
            PlayerFactory = playerFactory,
            Players = players,
        };
    }
}
