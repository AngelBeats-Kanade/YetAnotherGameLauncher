using System.Diagnostics;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Themes;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>主窗口：加载 games.json 构建游戏列表、页面切换与主题选择。</summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly GameCatalogService _catalogService;
    private readonly Func<string, IGameChannelApi?> _channelResolver;
    private readonly GameUpdateService _updateService;
    private readonly GameLauncherService _launcherService;
    private readonly ThemeService _themeService;
    private readonly Func<string?>? _defaultConfigTemplateFactory;

    public MainWindowViewModel(
        GameCatalogService catalogService,
        GameUpdateService updateService,
        GameLauncherService launcherService,
        ThemeService themeService,
        Func<string, IGameChannelApi?> channelResolver,
        Func<string?>? defaultConfigTemplateFactory = null)
    {
        _catalogService = catalogService;
        _updateService = updateService;
        _launcherService = launcherService;
        _themeService = themeService;
        _channelResolver = channelResolver;
        _defaultConfigTemplateFactory = defaultConfigTemplateFactory;
        SelectedTheme = ThemeModes[0];
        CurrentPage = null;
    }

    public ObservableCollection<GameItemViewModel> Games { get; } = [];

    [ObservableProperty]
    private GameItemViewModel? _selectedGame;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _configError;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>非错误类提示（如首次运行生成配置）以次要点色展示。</summary>
    public bool ShowStatusAsHint => HasStatusMessage && !ConfigError;

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatusMessage));
        OnPropertyChanged(nameof(ShowStatusAsHint));
    }

    partial void OnConfigErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStatusAsHint));
    }

    [ObservableProperty]
    private ThemeOption? _selectedTheme;

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (value is not null)
        {
            _themeService.Apply(value.Mode);
        }
    }

    public IReadOnlyList<ThemeOption> ThemeModes { get; } =
    [
        new ThemeOption(ThemeMode.System, "跟随系统"),
        new ThemeOption(ThemeMode.Light, "亮色"),
        new ThemeOption(ThemeMode.Dark, "暗色"),
    ];

    public string ConfigFilePath => _catalogService.ConfigFilePath;

    public string InstallRoot => _catalogService.Catalog?.Settings.InstallRoot ?? "";

    public string GameCountText => $"{Games.Count} 款游戏";

    partial void OnSelectedGameChanged(GameItemViewModel? value)
    {
        if (value is not null)
        {
            CurrentPage = value;
            _ = value.RefreshAsync();
        }
    }

    /// <summary>启动时初始化：加载配置 → 构建游戏列表 → 应用主题。失败时给出可读提示而不崩溃。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        StatusMessage = "";
        ConfigError = false;
        var catalog = new GameCatalog();
        try
        {
            await _catalogService.LoadAsync(cancellationToken);
            catalog = _catalogService.Catalog!;
            ConfigError = false;
        }
        catch (FileNotFoundException)
        {
            // 首次运行：在默认位置生成默认配置文件（优先使用随应用分发的模板），随后加载
            await _catalogService.CreateDefaultFileAsync(_defaultConfigTemplateFactory?.Invoke(), cancellationToken);
            await _catalogService.LoadAsync(cancellationToken);
            catalog = _catalogService.Catalog!;
            ConfigError = false;
            StatusMessage = $"已生成默认配置文件：{ConfigFilePath}，可参照 docs/GAME_CONFIG.md 编辑添加更多游戏。";
        }
        catch (GameCatalogValidationException ex)
        {
            ConfigError = true;
            StatusMessage = ex.Message;
            _catalogService.Catalog = catalog;
        }

        _themeService.Apply(catalog.Settings.Theme);
        SelectedTheme = ThemeModes.FirstOrDefault(t => t.Mode == catalog.Settings.Theme) ?? ThemeModes[0];

        var unknownChannels = new List<string>();
        foreach (var game in catalog.Games)
        {
            if (_channelResolver(game.Channel) is not { } channel)
            {
                unknownChannels.Add($"{game.DisplayName}（channel={game.Channel}）");
                continue;
            }

            Games.Add(new GameItemViewModel(
                game,
                InstallPath.Resolve(catalog.Settings.InstallRoot, game.InstallDir),
                channel,
                _updateService,
                _launcherService));
        }

        if (unknownChannels.Count > 0)
        {
            StatusMessage = $"以下游戏使用了未注册的渠道，已跳过：{string.Join("、", unknownChannels)}";
        }

        SelectedGame = Games.FirstOrDefault();
        CurrentPage = SelectedGame;
        OnPropertyChanged(nameof(GameCountText));
        OnPropertyChanged(nameof(InstallRoot));
    }

    [RelayCommand]
    private void ShowSettings() => CurrentPage = new SettingsViewModel(this);

    [RelayCommand]
    private void ShowGames() => CurrentPage = SelectedGame;
}

/// <summary>设置页：配置文件位置与主题（只读展示，编辑走 games.json 文档流程）。</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _owner;

    public SettingsViewModel(MainWindowViewModel owner) => _owner = owner;

    public string ConfigFilePath => _owner.ConfigFilePath;

    public string InstallRoot => _owner.InstallRoot;

    public string StatusMessage => _owner.StatusMessage;

    [RelayCommand]
    private void ShowGames() => _owner.ShowGamesCommand.Execute(null);

    [RelayCommand]
    private void OpenConfigFolder()
    {
        var directory = Path.GetDirectoryName(ConfigFilePath);
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        // 打开目录：跨平台由系统 shell 决定
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", directory);
        }
        else if (OperatingSystem.IsLinux())
        {
            Process.Start("xdg-open", directory);
        }
    }
}

/// <summary>主题选项（枚举 + 界面显示名）。</summary>
public sealed record ThemeOption(ThemeMode Mode, string DisplayName);
