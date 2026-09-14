using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Channels.Hypergryph;
using YetAnotherGameLauncher.Channels.Kuro;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.Themes;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher;

[ExcludeFromCodeCoverage]
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avalonia 模板默认的重复 DataContext 校验在此不必要：全部 ViewModel 均为 ObservableObject
            var services = BuildServices();
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            // 非"关窗"路径的程序性 Shutdown 也停视频（点 X 关闭已由窗口 Closing 覆盖）：
            // 退出期平台拆除会弄坏 GPU 解码栈，解码循环必须先行停止
            var videoPlayer = services.GetRequiredService<IVideoBackdropPlayer>();
            desktop.ShutdownRequested += (_, _) => videoPlayer.Stop();
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>组合根：全部服务的 DI 注册（UI 只依赖 Core 抽象与渠道实现）。</summary>
    public static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // Debug 构建输出 Debug 级日志便于排查;Release 构建从 Info 起减少控制台噪音
        services.AddLogging(builder => builder
#if DEBUG
            .SetMinimumLevel(LogLevel.Debug)
#else
            .SetMinimumLevel(LogLevel.Information)
#endif
            .AddSimpleConsole(options => options.SingleLine = true));

        // 基础设施（代理管理器持有共享 SocketsHttpHandler：设置保存后代理即时生效）
        services.AddSingleton<NetworkProxyManager>();
        services.AddSingleton(sp =>
        {
            var proxyManager = sp.GetRequiredService<NetworkProxyManager>();
            return new HttpClient(proxyManager.Handler)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
        });
        services.AddSingleton<SpeedLimiter>();
        services.AddSingleton<HttpFileDownloader>();
        services.AddSingleton<IDownloader>(sp => sp.GetRequiredService<HttpFileDownloader>());
        services.AddSingleton<IProcessRunner>(sp =>
            new SystemProcessRunner(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<SystemProcessRunner>(),
                supportsElevationRetry: OperatingSystem.IsWindows()));
        services.AddSingleton<IPlatformInfo>(sp =>
            OperatingSystem.IsLinux()
                ? new LinuxPlatformInfo()
                : new WindowsPlatformInfo());
        services.AddSingleton<IAutostartService>(sp => OperatingSystem.IsLinux()
            ? new LinuxAutostartService()
            : new WindowsAutostartService(sp.GetRequiredService<IProcessRunner>()));
        services.AddSingleton<IPatchApplier>(sp => new HpatchzApplier(sp.GetRequiredService<IProcessRunner>()));

        // 渠道（keyed by games.json 的 game.channel）
        services.AddKuroChannel();
        services.AddHypergryphChannel();

        // 领域服务
        services.AddSingleton(sp => new GameCatalogService(AppPaths.GetConfigFilePath()));
        services.AddSingleton<GameUpdateService>();
        services.AddSingleton<GameLauncherService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<BackgroundImageService>();
        services.AddSingleton<IFilePickerService, StorageProviderFilePicker>();

        // 背景视频（FFmpeg 解码；原生库缺失时自动降级静态海报）
        services.AddSingleton<FfmpegLibraryResolver>();
        services.AddSingleton<IVideoBackdropPlayer, FfmpegVideoBackdropPlayer>();

        // Linux 兼容层：原生 umu（内置 C#）+ 外部 umu-run 引导安装（回退）
        services.AddSingleton(sp => new UmuLauncherInstaller(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<HttpFileDownloader>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<UmuLauncherInstaller>()));
        services.AddSingleton<IUmuComponentProvisioner>(sp => new UmuComponentProvisioner(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IDownloader>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<UmuComponentProvisioner>()));
        services.AddSingleton(sp => new NativeUmuLauncher(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<IUmuComponentProvisioner>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<NativeUmuLauncher>()));

        // 游戏背景解析（按渠道键注册；配置文件不携带背景地址，启动时向渠道确认当期背景）
        services.AddSingleton<KuroSwitchConfigClient>();
        services.AddSingleton<KuroGachaService>();
        services.AddKeyedSingleton<IBackdropResolver, KuroBackdropResolver>("kuro");
        services.AddKeyedSingleton<IBackdropResolver, EndfieldBackdropResolver>("hypergryph");
        services.AddSingleton(sp => new GameBackdropService(
            sp.GetRequiredService<HttpClient>(),
            new Dictionary<string, IBackdropResolver>
            {
                ["kuro"] = sp.GetRequiredKeyedService<IBackdropResolver>("kuro"),
                ["hypergryph"] = sp.GetRequiredKeyedService<IBackdropResolver>("hypergryph"),
            },
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<GameBackdropService>()));

        // ViewModel
        services.AddSingleton(sp =>
        {
            var keyed = (IKeyedServiceProvider)sp;
            return new MainWindowViewModel(
                sp.GetRequiredService<GameCatalogService>(),
                sp.GetRequiredService<GameUpdateService>(),
                sp.GetRequiredService<GameLauncherService>(),
                sp.GetRequiredService<HttpFileDownloader>(),
                sp.GetRequiredService<IAutostartService>(),
                sp.GetRequiredService<ThemeService>(),
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetRequiredService<BackgroundImageService>(),
                sp.GetRequiredService<GameBackdropService>(),
                channelKey => keyed.GetKeyedService<IGameChannelApi>(channelKey),
                TryLoadEmbeddedSampleTemplate,
                sp.GetRequiredService<IFilePickerService>(),
                sp.GetRequiredService<IVideoBackdropPlayer>(),
                sp.GetRequiredService<KuroGachaService>(),
                umuInstaller: sp.GetRequiredService<UmuLauncherInstaller>(),
                nativeUmu: sp.GetRequiredService<NativeUmuLauncher>(),
                umuProvisioner: sp.GetRequiredService<IUmuComponentProvisioner>());
        });

        return services.BuildServiceProvider();
    }

    /// <summary>读取内嵌的默认配置模板（samples/games.json）；缺失或损坏时返回 null，回退到最小默认。</summary>
    private static string? TryLoadEmbeddedSampleTemplate()
    {
        try
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream("games.sample.json");
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
