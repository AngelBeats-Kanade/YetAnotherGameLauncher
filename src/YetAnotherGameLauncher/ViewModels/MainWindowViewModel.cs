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

    public MainWindowViewModel(
        GameCatalogService catalogService,
        GameUpdateService updateService,
        GameLauncherService launcherService,
        ThemeService themeService,
        Func<string, IGameChannelApi?> channelResolver)
    {
        _catalogService = catalogService;
        _updateService = updateService;
        _launcherService = launcherService;
        _themeService = themeService;
        _channelResolver = channelResolver;
        SelectedTheme = _themeService.Mode;
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

    [ObservableProperty]
    private ThemeMode _selectedTheme;

    partial void OnSelectedThemeChanged(ThemeMode value) => _themeService.Apply(value);

    public IReadOnlyList<ThemeMode> ThemeModes { get; } = [ThemeMode.System, ThemeMode.Light, ThemeMode.Dark];

    public string ConfigFilePath => _catalogService.ConfigFilePath;

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
        var catalog = new GameCatalog();
        try
        {
            await _catalogService.LoadAsync(cancellationToken);
            catalog = _catalogService.Catalog!;
            ConfigError = false;
        }
        catch (FileNotFoundException)
        {
            ConfigError = true;
            StatusMessage = $"未找到配置文件 {ConfigFilePath}，请参照 docs/GAME_CONFIG.md 创建（可复制 samples/games.json）。";
            _catalogService.Catalog = catalog;
        }
        catch (GameCatalogValidationException ex)
        {
            ConfigError = true;
            StatusMessage = ex.Message;
            _catalogService.Catalog = catalog;
        }

        _themeService.Apply(catalog.Settings.Theme);
        SelectedTheme = _themeService.Mode;
        OnPropertyChanged(nameof(SelectedTheme));

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
