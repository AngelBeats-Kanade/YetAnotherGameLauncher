using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Channels.Hypergryph;
using YetAnotherGameLauncher.Channels.Kuro;
using YetAnotherGameLauncher.Themes;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher;

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
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>组合根：全部服务的 DI 注册（UI 只依赖 Core 抽象与渠道实现）。</summary>
    public static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging();

        // 基础设施
        services.AddSingleton(_ => new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        });
        services.AddSingleton<IDownloader, HttpFileDownloader>();
        services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        services.AddSingleton<IPatchApplier>(sp => new HpatchzApplier(sp.GetRequiredService<IProcessRunner>()));

        // 渠道（keyed by games.json 的 game.channel）
        services.AddKuroChannel();
        services.AddHypergryphChannel();

        // 领域服务
        services.AddSingleton(sp => new GameCatalogService(AppPaths.GetConfigFilePath()));
        services.AddSingleton<GameUpdateService>();
        services.AddSingleton<GameLauncherService>();
        services.AddSingleton<ThemeService>();

        // ViewModel
        services.AddSingleton(sp =>
        {
            var keyed = (IKeyedServiceProvider)sp;
            return new MainWindowViewModel(
                sp.GetRequiredService<GameCatalogService>(),
                sp.GetRequiredService<GameUpdateService>(),
                sp.GetRequiredService<GameLauncherService>(),
                sp.GetRequiredService<ThemeService>(),
                channelKey => keyed.GetKeyedService<IGameChannelApi>(channelKey));
        });

        return services.BuildServiceProvider();
    }
}
