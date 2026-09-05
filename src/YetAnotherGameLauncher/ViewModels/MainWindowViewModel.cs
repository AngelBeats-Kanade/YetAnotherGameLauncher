using System.ComponentModel;
using System.Diagnostics;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.Themes;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>主窗口：加载 games.json 构建游戏列表、页面切换与主题/语言设置。</summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly GameCatalogService _catalogService;
    private readonly Func<string, IGameChannelApi?> _channelResolver;
    private readonly GameUpdateService _updateService;
    private readonly GameLauncherService _launcherService;
    private readonly ThemeService _themeService;
    private readonly ILocalizationService _loc;
    private readonly Func<string?>? _defaultConfigTemplateFactory;

    public MainWindowViewModel(
        GameCatalogService catalogService,
        GameUpdateService updateService,
        GameLauncherService launcherService,
        ThemeService themeService,
        ILocalizationService localization,
        Func<string, IGameChannelApi?> channelResolver,
        Func<string?>? defaultConfigTemplateFactory = null)
    {
        _catalogService = catalogService;
        _updateService = updateService;
        _launcherService = launcherService;
        _themeService = themeService;
        _loc = localization;
        _channelResolver = channelResolver;
        _defaultConfigTemplateFactory = defaultConfigTemplateFactory;
        Loc = localization;
        LocBridge.Instance = localization; // 供 {svc:Loc key} 标记扩展取 Source
        _loc.PropertyChanged += OnLanguageChanged;
        RebuildThemeModes(keepMode: null);
        SelectedTheme = ThemeModes[0];
        CurrentPage = null;
    }

    /// <summary>暴露给 XAML 的文案服务：{Binding Loc[key]} 在语言切换时整体刷新。</summary>
    public ILocalizationService Loc { get; }

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

    public IReadOnlyList<ThemeOption> ThemeModes { get; private set; } = [];

    private void RebuildThemeModes(ThemeMode? keepMode)
    {
        ThemeModes =
        [
            new ThemeOption(ThemeMode.System, _loc["theme_system"]),
            new ThemeOption(ThemeMode.Light, _loc["theme_light"]),
            new ThemeOption(ThemeMode.Dark, _loc["theme_dark"]),
        ];
        OnPropertyChanged(nameof(ThemeModes));
        if (keepMode is { } mode)
        {
            SelectedTheme = ThemeModes.FirstOrDefault(t => t.Mode == mode) ?? ThemeModes[0];
        }
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        RebuildThemeModes(SelectedTheme?.Mode);
        OnPropertyChanged(nameof(GameCountText));
        foreach (var game in Games)
        {
            _ = game.RefreshAsync();
        }
    }

    public string ConfigFilePath => _catalogService.ConfigFilePath;

    public string InstallRoot => _catalogService.Catalog?.Settings.InstallRoot ?? "";

    public string GameCountText => _loc.Format("sidebar_games_count", Games.Count);

    /// <summary>侧栏展开/收起（收起 = 68px 图标窄条），宽度驱动侧栏过渡动画。</summary>
    public const double SidebarExpandedWidth = 264;

    public const double SidebarCollapsedWidth = 68;

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    public double SidebarWidth => IsSidebarExpanded ? SidebarExpandedWidth : SidebarCollapsedWidth;

    partial void OnIsSidebarExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarWidth));
        _ = PersistSidebarExpandedAsync(value);
    }

    private async Task PersistSidebarExpandedAsync(bool expanded)
    {
        if (_catalogService.Catalog is not { } catalog || catalog.Settings.SidebarExpanded == expanded)
        {
            return;
        }

        catalog.Settings.SidebarExpanded = expanded;
        try
        {
            await _catalogService.SaveAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConfigError = true;
            StatusMessage = _loc.Format("message_saveFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    [RelayCommand]
    private void ShowAbout() => CurrentPage = new AboutViewModel(this);

    partial void OnSelectedGameChanged(GameItemViewModel? value)
    {
        if (value is not null)
        {
            CurrentPage = value;
            _ = value.RefreshAsync();
        }
    }

    /// <summary>启动时初始化：加载配置 → 应用语言/主题 → 构建游戏列表。失败时给出可读提示而不崩溃。</summary>
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
            StatusMessage = _loc.Format("message_configCreated", ConfigFilePath);
        }
        catch (GameCatalogValidationException ex)
        {
            ConfigError = true;
            StatusMessage = ex.Message;
            _catalogService.Catalog = catalog;
        }

        _loc.SetLanguage(catalog.Settings.Language);
        _themeService.Apply(catalog.Settings.Theme);
        SelectedTheme = ThemeModes.FirstOrDefault(t => t.Mode == catalog.Settings.Theme) ?? ThemeModes[0];
        IsSidebarExpanded = catalog.Settings.SidebarExpanded;

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
                _launcherService,
                _loc));
        }

        if (unknownChannels.Count > 0)
        {
            StatusMessage = _loc.Format("message_unknownChannels", string.Join(", ", unknownChannels));
        }

        SelectedGame = Games.FirstOrDefault();
        CurrentPage = SelectedGame;
        OnPropertyChanged(nameof(GameCountText));
        OnPropertyChanged(nameof(InstallRoot));
    }

    /// <summary>把语言设置写回 games.json（UI 切换由 SetLanguage 即时生效，此处只负责持久化）。</summary>
    public async Task SaveLanguageAsync(string language, CancellationToken cancellationToken = default)
    {
        if (_catalogService.Catalog is not { } catalog || catalog.Settings.Language == language)
        {
            return;
        }

        catalog.Settings.Language = language;
        try
        {
            await _catalogService.SaveAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConfigError = true;
            StatusMessage = _loc.Format("message_saveFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void ShowSettings() => CurrentPage = new SettingsViewModel(this);

    [RelayCommand]
    private void ShowGames() => CurrentPage = SelectedGame;
}

/// <summary>设置页：外观（主题/语言）、配置文件与下载设置（其余编辑走 games.json）。</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _owner;

    public SettingsViewModel(MainWindowViewModel owner) => _owner = owner;

    public ILocalizationService Loc => _owner.Loc;

    public string ConfigFilePath => _owner.ConfigFilePath;

    public string InstallRoot => _owner.InstallRoot;

    public string StatusMessage => _owner.StatusMessage;

    public IReadOnlyList<ThemeOption> ThemeModes => _owner.ThemeModes;

    public ThemeOption? SelectedTheme
    {
        get => _owner.SelectedTheme;
        set
        {
            if (value is not null && value != _owner.SelectedTheme)
            {
                _owner.SelectedTheme = value;
                OnPropertyChanged();
            }
        }
    }

    public IReadOnlyList<LanguageOption> Languages => BuildLanguages();

    private IReadOnlyList<LanguageOption> BuildLanguages() =>
    [
        new LanguageOption(LocalizationService.SystemLanguage, _owner.Loc["lang_system"]),
        new LanguageOption("zh-CN", _owner.Loc["lang_zh-CN"]),
        new LanguageOption("en-US", _owner.Loc["lang_en-US"]),
    ];

    public LanguageOption? SelectedLanguage
    {
        get => BuildLanguages().FirstOrDefault(l => l.Value == _owner.Loc.Language);
        set
        {
            if (value is null || value.Value == _owner.Loc.Language)
            {
                return;
            }

            _owner.Loc.SetLanguage(value.Value); // 触发全局 Item[] 刷新
            OnPropertyChanged(); // 下拉框选中项回显
            OnPropertyChanged(nameof(Languages)); // "跟随系统" 等显示名随语言重建
            _ = _owner.SaveLanguageAsync(value.Value);
        }
    }

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

/// <summary>语言选项（配置值 + 显示名；语言名本身不翻译）。</summary>
public sealed record LanguageOption(string Value, string DisplayName);

/// <summary>关于页的第三方组件条目。</summary>
public sealed record ThirdPartyItem(string Name, string License);

/// <summary>关于页：应用信息、版本与第三方声明。</summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _owner;

    public AboutViewModel(MainWindowViewModel owner) => _owner = owner;

    public ILocalizationService Loc => _owner.Loc;

    public string ConfigFilePath => _owner.ConfigFilePath;

    public string GameCountText => _owner.GameCountText;

    /// <summary>应用程序集版本（AssemblyInformationalVersion 优先，含 git 信息时更长）。</summary>
    public string AppVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    public string RuntimeVersion =>
        Environment.Version.ToString();

    public IReadOnlyList<ThirdPartyItem> ThirdParty =>
    [
        new("Avalonia UI 12.1.2", "MIT"),
        new("CommunityToolkit.Mvvm 8.4.2", "MIT"),
        new(".NET 10 / Microsoft.Extensions.*", "MIT"),
        new("HDiffPatch (hpatchz)", "Apache-2.0"),
        new("ui-ux-pro-max design data", "MIT"),
        new("frontend-design skill", "Apache-2.0"),
    ];

    [RelayCommand]
    private void ShowGames() => _owner.ShowGamesCommand.Execute(null);
}
