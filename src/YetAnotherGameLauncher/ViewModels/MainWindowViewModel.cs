using System.ComponentModel;
using System.Diagnostics;
using System.Collections.ObjectModel;
using Avalonia.Media;
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
    private readonly HttpFileDownloader _downloader;
    private readonly IAutostartService _autostart;
    private readonly ThemeService _themeService;
    private readonly ILocalizationService _loc;
    private readonly BackgroundImageService _backgroundImageService;
    private readonly GameBackdropService _backdropService;
    private readonly IFilePickerService? _filePicker;
    private readonly Func<string?>? _defaultConfigTemplateFactory;

    public MainWindowViewModel(
        GameCatalogService catalogService,
        GameUpdateService updateService,
        GameLauncherService launcherService,
        HttpFileDownloader downloader,
        IAutostartService autostart,
        ThemeService themeService,
        ILocalizationService localization,
        BackgroundImageService backgroundImageService,
        GameBackdropService backdropService,
        Func<string, IGameChannelApi?> channelResolver,
        Func<string?>? defaultConfigTemplateFactory = null,
        IFilePickerService? filePicker = null)
    {
        _catalogService = catalogService;
        _updateService = updateService;
        _launcherService = launcherService;
        _downloader = downloader;
        _autostart = autostart;
        _themeService = themeService;
        _loc = localization;
        _backgroundImageService = backgroundImageService;
        _backdropService = backdropService;
        _channelResolver = channelResolver;
        _defaultConfigTemplateFactory = defaultConfigTemplateFactory;
        _filePicker = filePicker;
        Loc = localization;
        LocBridge.Instance = localization; // 供 {svc:Loc key} 标记扩展取 Source
        _loc.PropertyChanged += OnLanguageChanged;
        RebuildThemeModes(keepMode: null);
        SelectedTheme = ThemeModes[0];
        CurrentPage = null;
    }

    /// <summary>暴露给 XAML 的文案服务：{Binding Loc[key]} 在语言切换时整体刷新。</summary>
    public ILocalizationService Loc { get; }

    /// <summary>游戏列表（按配置顺序构建；侧栏与导航的数据源）。</summary>
    public ObservableCollection<GameItemViewModel> Games { get; } = [];

    /// <summary>当前选中的游戏；变化时导航到详情页并刷新状态。</summary>
    [ObservableProperty]
    private GameItemViewModel? _selectedGame;

    /// <summary>当前显示的页面（游戏详情/游戏设置/应用设置/关于）。</summary>
    [ObservableProperty]
    private object? _currentPage;

    /// <summary>导航方向：false=前进（新页自右滑入），true=后退（自左滑入），驱动页面切换动画。</summary>
    [ObservableProperty]
    private bool _isNavBack;

    /// <summary>侧栏高亮归属：当前页属于游戏（详情/游戏设置）。</summary>
    public bool IsGameNavActive => CurrentPage is GameItemViewModel or GameSettingsViewModel;

    /// <summary>侧栏高亮归属：当前页是应用设置页。</summary>
    public bool IsSettingsNavActive => CurrentPage is SettingsViewModel;

    /// <summary>侧栏高亮归属：当前页是关于页。</summary>
    public bool IsAboutNavActive => CurrentPage is AboutViewModel;

    /// <summary>
    /// 侧栏游戏列表的选中项转发：非游戏页时返回 null 让高亮让位给设置/关于入口。
    /// 停在设置/关于页时点击"仍是当前游戏"的列表项不会触发 SelectedGame 变化，这里直接导航回去。
    /// </summary>
    public GameItemViewModel? GameNavSelection
    {
        get => IsGameNavActive ? SelectedGame : null;
        set
        {
            if (value is null)
            {
                return;
            }

            if (!ReferenceEquals(value, SelectedGame))
            {
                SelectedGame = value;
            }
            else
            {
                NavigateTo(value);
            }
        }
    }

    /// <summary>页面切换时触发：转发通知给依赖 CurrentPage 的侧栏高亮与选中项绑定。</summary>
    partial void OnCurrentPageChanged(object? value)
    {
        OnPropertyChanged(nameof(IsGameNavActive));
        OnPropertyChanged(nameof(IsSettingsNavActive));
        OnPropertyChanged(nameof(IsAboutNavActive));
        OnPropertyChanged(nameof(GameNavSelection));
    }

    /// <summary>切换当前页并记录方向（决定切换动画自左/自右滑入）。</summary>
    private void NavigateTo(object? page, bool back = false)
    {
        if (ReferenceEquals(CurrentPage, page))
        {
            return;
        }

        IsNavBack = back;
        CurrentPage = page;
    }

    /// <summary>全局状态栏提示文本（配置生成/迁移/操作结果等）。</summary>
    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>当前状态提示是否为错误（驱动侧栏红字样式）。</summary>
    [ObservableProperty]
    private bool _configError;

    /// <summary>是否有待展示的状态提示（控制状态栏可见性）。</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>应用自有背景图（设置/关于/侧栏底色，来自设置的背景图选项）；null = 内置主题渐变。</summary>
    [ObservableProperty]
    private IImage? _appBackgroundImage;

    /// <summary>是否已设置应用自定义背景图（否则回退内置主题渐变）。</summary>
    public bool HasCustomAppBackground => AppBackgroundImage is not null;

    /// <summary>背景图变化时触发：转发通知给 HasCustomAppBackground。</summary>
    partial void OnAppBackgroundImageChanged(IImage? value) => OnPropertyChanged(nameof(HasCustomAppBackground));

    /// <summary>非错误类提示（如首次运行生成配置）以次要点色展示。</summary>
    public bool ShowStatusAsHint => HasStatusMessage && !ConfigError;

    /// <summary>状态提示变化时触发：转发通知给可见性与样式绑定。</summary>
    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatusMessage));
        OnPropertyChanged(nameof(ShowStatusAsHint));
    }

    /// <summary>错误标记变化时触发：转发通知给 ShowStatusAsHint。</summary>
    partial void OnConfigErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStatusAsHint));
    }

    /// <summary>当前选中的主题选项（主窗口与设置页共用）；变化即应用主题。</summary>
    [ObservableProperty]
    private ThemeOption? _selectedTheme;

    /// <summary>主题选项变化时触发：立即应用对应的 ThemeVariant。</summary>
    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (value is not null)
        {
            _themeService.Apply(value.Mode);
        }
    }

    /// <summary>可选主题列表（显示名随语言重建）。</summary>
    public IReadOnlyList<ThemeOption> ThemeModes { get; private set; } = [];

    /// <summary>按当前语言重建主题显示名；keepMode 非空时保持选中模式不变。</summary>
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

    /// <summary>语言切换回调：重建主题/计数文案并刷新各游戏，强制重建当前页让 {svc:Loc} 取到新语言。</summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        RebuildThemeModes(SelectedTheme?.Mode);
        OnPropertyChanged(nameof(GameCountText));
        foreach (var game in Games)
        {
            _ = game.RefreshAsync();
        }

        // 兜底：强制重建当前页面，保证所有 {svc:Loc} 标记扩展拿到新语言
        var page = CurrentPage;
        CurrentPage = null;
        CurrentPage = page;
    }

    /// <summary>当前下载限速（字节/秒；0 = 不限速），供设置页草稿初始化。</summary>
    public long DownloadSpeedLimitBytes => _catalogService.Catalog?.Settings.DownloadSpeedLimitBytes ?? 0;

    /// <summary>games.json 配置文件完整路径（展示与"打开所在目录"用）。</summary>
    public string ConfigFilePath => _catalogService.ConfigFilePath;

    /// <summary>当前安装根目录（供设置页草稿初始化与展示）。</summary>
    public string InstallRoot => _catalogService.Catalog?.Settings.InstallRoot ?? "";

    /// <summary>侧栏游戏计数文案（随语言切换刷新）。</summary>
    public string GameCountText => _loc.Format("sidebar_games_count", Games.Count);

    /// <summary>侧栏展开/收起（收起 = 68px 图标窄条），宽度驱动侧栏过渡动画。</summary>
    private const double SidebarExpandedWidth = 264;

    /// <summary>侧栏收起时的窄条宽度。</summary>
    private const double SidebarCollapsedWidth = 68;

    /// <summary>侧栏是否展开（持久化到 games.json，启动时恢复）。</summary>
    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    /// <summary>侧栏当前宽度（展开/收起值二选一，驱动过渡动画）。</summary>
    public double SidebarWidth => IsSidebarExpanded ? SidebarExpandedWidth : SidebarCollapsedWidth;

    /// <summary>展开状态变化时触发：转发通知给 SidebarWidth 并异步持久化。</summary>
    partial void OnIsSidebarExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarWidth));
        _ = PersistSidebarExpandedAsync(value);
    }

    /// <summary>
    /// 保存 games.json 并统一处理 IO 失败：成功返回 true；
    /// 失败置 ConfigError/StatusMessage（侧栏红字提示）后返回 false。
    /// </summary>
    public async Task<bool> TrySaveCatalogAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _catalogService.SaveAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConfigError = true;
            StatusMessage = _loc.Format("message_saveFailed", ex.Message);
            return false;
        }
    }

    /// <summary>把侧栏展开状态写回 games.json（值未变或无配置时跳过）。</summary>
    private async Task PersistSidebarExpandedAsync(bool expanded)
    {
        if (_catalogService.Catalog is not { } catalog || catalog.Settings.SidebarExpanded == expanded)
        {
            return;
        }

        catalog.Settings.SidebarExpanded = expanded;
        await TrySaveCatalogAsync();
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    [RelayCommand]
    private void ShowAbout() => NavigateTo(new AboutViewModel(this));

    [RelayCommand]
    private void ShowGameSettings()
    {
        if (SelectedGame is not null)
        {
            NavigateTo(new GameSettingsViewModel(SelectedGame, this));
        }
    }

    /// <summary>选中游戏变化时触发：转发侧栏选中通知，非空时导航到详情页并刷新状态。</summary>
    partial void OnSelectedGameChanged(GameItemViewModel? value)
    {
        OnPropertyChanged(nameof(GameNavSelection));
        if (value is not null)
        {
            NavigateTo(value);
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

        await MigrateFromSampleAsync(catalog, cancellationToken);
        _downloader.Limiter.BytesPerSecond = catalog.Settings.DownloadSpeedLimitBytes;

        _loc.SetLanguage(catalog.Settings.Language);
        _themeService.Apply(catalog.Settings.Theme);
        SelectedTheme = ThemeModes.FirstOrDefault(t => t.Mode == catalog.Settings.Theme) ?? ThemeModes[0];
        IsSidebarExpanded = catalog.Settings.SidebarExpanded;
        _ = LoadAppBackgroundAsync();

        var unknownChannels = RebuildGames(catalog);

        if (unknownChannels.Count > 0)
        {
            StatusMessage = _loc.Format("message_unknownChannels", string.Join("、", unknownChannels));
        }

        SelectedGame = Games.FirstOrDefault();
        NavigateTo(SelectedGame);
        OnPropertyChanged(nameof(GameCountText));
        OnPropertyChanged(nameof(InstallRoot));
    }

    /// <summary>按当前配置重建游戏列表（installRoot 变更后调用），返回未知渠道提示列表。</summary>
    private List<string> RebuildGames(GameCatalog catalog)
    {
        Games.Clear();
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
                _loc,
                _catalogService,
                _backgroundImageService,
                _backdropService));
        }

        return unknownChannels;
    }

    /// <summary>启动与设置变更后加载应用自定义背景图；失败静默回退内置渐变。</summary>
    private async Task LoadAppBackgroundAsync()
    {
        var source = _catalogService.Catalog?.Settings.AppBackgroundImage;
        var image = await _backgroundImageService.LoadAsync(source);
        AppBackgroundImage = image;
    }

    /// <summary>
    /// 设置应用自定义背景图（null/空白 = 恢复内置渐变）并持久化，成功返回 true。
    /// </summary>
    public async Task<bool> SetAppBackgroundAsync(string? path)
    {
        if (_catalogService.Catalog is not { } catalog)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
        {
            return false;
        }

        catalog.Settings.AppBackgroundImage = string.IsNullOrWhiteSpace(path) ? null : path;
        if (!await TrySaveCatalogAsync())
        {
            return false;
        }

        AppBackgroundImage = await _backgroundImageService.LoadAsync(catalog.Settings.AppBackgroundImage);
        return true;
    }

    /// <summary>配置文件里记录的应用自定义背景路径（空白 = 内置渐变）。</summary>
    public string ConfiguredAppBackground => _catalogService.Catalog?.Settings.AppBackgroundImage ?? "";

    /// <summary>弹系统文件选择器挑一张图片作为应用背景；未注册选择器（测试）时返回 null。</summary>
    public async Task<string?> PickImageFileAsync()
    {
        if (_filePicker is null)
        {
            return null;
        }

        return await _filePicker.PickImageFileAsync(_loc["settings_appBackground_pickTitle"]);
    }

    /// <summary>应用下载限速（字节/秒）并持久化；0 = 不限速。</summary>
    public async Task ApplySpeedLimitAsync(long bytesPerSecond, CancellationToken cancellationToken = default)
    {
        _downloader.Limiter.BytesPerSecond = bytesPerSecond;
        if (_catalogService.Catalog is { } catalog)
        {
            catalog.Settings.DownloadSpeedLimitBytes = bytesPerSecond;
            await TrySaveCatalogAsync(cancellationToken);
        }
    }

    /// <summary>异步查询自启状态（Windows 需起 reg 子进程，禁止在 UI 线程同步等待）。</summary>
    public Task<bool> GetAutostartStateAsync(CancellationToken cancellationToken = default) =>
        _autostart.IsEnabledAsync(cancellationToken);

    /// <summary>切换开机自启动，成功返回 true（失败时置状态提示）。</summary>
    public async Task<bool> SetAutostartAsync(bool enabled)
    {
        try
        {
            await _autostart.SetEnabledAsync(enabled);
            return await _autostart.IsEnabledAsync() == enabled;
        }
        catch (Exception ex)
        {
            ConfigError = true;
            StatusMessage = _loc.Format("message_saveFailed", ex.Message);
            return false;
        }
    }

    /// <summary>更新安装根目录并重建游戏列表（路径重新解析），成功返回 true。</summary>
    public async Task<bool> UpdateInstallRootAsync(string newRoot)
    {
        if (_catalogService.Catalog is not { } catalog)
        {
            return false;
        }

        var root = newRoot.Trim();
        if (root.Length == 0)
        {
            return false;
        }

        catalog.Settings.InstallRoot = root;
        if (!await TrySaveCatalogAsync())
        {
            return false;
        }

        // 设置页/关于页保持当前页；游戏详情则回到重建后的第一个游戏
        var keepPage = CurrentPage is SettingsViewModel or AboutViewModel ? CurrentPage : null;
        SelectedGame = null;
        RebuildGames(catalog);
        SelectedGame = Games.FirstOrDefault();
        NavigateTo(keepPage ?? SelectedGame);
        OnPropertyChanged(nameof(InstallRoot));
        return true;
    }

    /// <summary>
    /// 旧配置一次性迁移：与内置样例模板对照，补齐同一游戏新增的官方服务器与本地化名称，
    /// 并写入 SchemaVersion 防止重复执行。
    /// </summary>
    private async Task MigrateFromSampleAsync(GameCatalog catalog, CancellationToken cancellationToken)
    {
        if (catalog.Settings.SchemaVersion >= 3)
        {
            return;
        }

        var templateJson = _defaultConfigTemplateFactory?.Invoke();
        if (templateJson is not null)
        {
            var sample = GameCatalogService.Parse(templateJson);
            var added = new List<string>();
            foreach (var game in catalog.Games)
            {
                MergeFromSample(game, sample.Games.FirstOrDefault(g => g.Id == game.Id), added);
            }

            if (added.Count > 0)
            {
                StatusMessage = _loc.Format("message_serversAdded", string.Join("、", added));
            }
        }

        catalog.Settings.SchemaVersion = 3;
        try
        {
            await _catalogService.SaveAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 迁移写回失败不致命：下次启动会再次尝试
        }
    }

    /// <summary>单个游戏的样例对照合并：补服务器（记录新增项）、图标与本地化名称（已有值不覆盖）。</summary>
    private void MergeFromSample(GameDefinition game, GameDefinition? sampleGame, List<string> added)
    {
        if (sampleGame is null)
        {
            return;
        }

        foreach (var server in sampleGame.Servers)
        {
            if (!game.Servers.Any(s => s.Id.Equals(server.Id, StringComparison.OrdinalIgnoreCase)))
            {
                game.Servers.Add(server);
                added.Add($"{game.DisplayName} · {server.Name}");
            }
        }

        if (string.IsNullOrWhiteSpace(game.Icon) && !string.IsNullOrWhiteSpace(sampleGame.Icon))
        {
            game.Icon = sampleGame.Icon;
        }

        if (game.NameLocalized.Count == 0 && sampleGame.NameLocalized.Count > 0)
        {
            foreach (var (culture, name) in sampleGame.NameLocalized)
            {
                game.NameLocalized[culture] = name;
            }
        }
    }

    /// <summary>把语言设置写回 games.json（UI 切换由 SetLanguage 即时生效，此处只负责持久化）。</summary>
    public async Task SaveLanguageAsync(string language, CancellationToken cancellationToken = default)
    {
        if (_catalogService.Catalog is not { } catalog || catalog.Settings.Language == language)
        {
            return;
        }

        catalog.Settings.Language = language;
        await TrySaveCatalogAsync(cancellationToken);
    }

    /// <summary>打开应用设置页，页面上屏后异步补齐自启状态。</summary>
    [RelayCommand]
    private async Task ShowSettingsAsync(CancellationToken cancellationToken)
    {
        var page = new SettingsViewModel(this);
        NavigateTo(page);
        // 自启状态需起 reg 子进程查询，页面上屏后异步补齐（此前同步阻塞求值导致 UI 死锁）
        await page.InitializeAsync(cancellationToken);
    }

    [RelayCommand]
    private void ShowGames() => NavigateTo(SelectedGame, back: true);
}

/// <summary>设置页：外观（主题/语言）、配置文件与下载设置（其余编辑走 games.json）。</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _owner;

    public SettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        _installRootDraft = owner.InstallRoot;
        _speedLimitMbDraft = FormatSpeed(owner.DownloadSpeedLimitBytes);
        _appBackgroundPath = owner.ConfiguredAppBackground;
    }

    private static string FormatSpeed(long bytes) =>
        bytes <= 0 ? "0" : Math.Round(bytes / 1024.0 / 1024.0, 1).ToString("0.#");

    /// <summary>文案服务（转发主窗口实例，供 XAML 绑定）。</summary>
    public ILocalizationService Loc => _owner.Loc;

    /// <summary>games.json 配置文件完整路径（展示与"打开所在目录"用）。</summary>
    public string ConfigFilePath => _owner.ConfigFilePath;

    /// <summary>安装根目录草稿（编辑后点保存生效，游戏路径随之重新解析）。</summary>
    [ObservableProperty]
    private string _installRootDraft;

    /// <summary>下载限速草稿（MB/s，0 = 不限速）；重新编辑时清空上次结果。</summary>
    [ObservableProperty]
    private string _speedLimitMbDraft;

    /// <summary>限速保存结果提示。</summary>
    [ObservableProperty]
    private SaveMessageSlot _speedLimitSave = new();

    partial void OnSpeedLimitMbDraftChanged(string value) => SpeedLimitSave.Clear();

    /// <summary>校验限速草稿（MB/s ≥ 0）并应用，结果写入独立消息位。</summary>
    [RelayCommand]
    private async Task SaveDownloadLimitAsync(CancellationToken cancellationToken)
    {
        SpeedLimitSave.Clear();
        if (!double.TryParse(SpeedLimitMbDraft.Trim(), out var mb) || mb < 0)
        {
            SpeedLimitSave.SetFailure(Loc["settings_downloadLimitInvalid"]);
            return;
        }

        await _owner.ApplySpeedLimitAsync((long)Math.Round(mb * 1024 * 1024), cancellationToken);
        SpeedLimitSave.SetSuccess(Loc["settings_downloadLimitSaved"]);
    }

    /// <summary>开机自启动开关（写入系统注册表 / XDG autostart），打开设置页时异步初始化。</summary>
    [ObservableProperty]
    private bool _isAutostart;

    /// <summary>自启设置结果提示（独立消息位，显示在自启开关旁）。</summary>
    [ObservableProperty]
    private SaveMessageSlot _autostartSave = new();

    /// <summary>页面上屏后异步补齐自启状态（Windows 查询需起 reg 子进程）。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        IsAutostart = await _owner.GetAutostartStateAsync(cancellationToken);

    /// <summary>切换自启并回读实际状态，失败时写入失败提示。</summary>
    public async Task SetAutostartAsync(bool enabled)
    {
        AutostartSave.Clear();
        if (!await _owner.SetAutostartAsync(enabled))
        {
            AutostartSave.SetFailure(Loc["settings_autostartFailed"]);
        }

        IsAutostart = await _owner.GetAutostartStateAsync();
    }

    [RelayCommand]
    private Task ToggleAutostartAsync() => SetAutostartAsync(!IsAutostart);

    /// <summary>安装根目录保存结果提示。</summary>
    [ObservableProperty]
    private SaveMessageSlot _installRootSave = new();

    partial void OnInstallRootDraftChanged(string value) => InstallRootSave.Clear();

    /// <summary>校验安装根目录草稿非空后保存，结果写入独立消息位。</summary>
    [RelayCommand]
    private async Task SaveInstallRootAsync(CancellationToken cancellationToken)
    {
        InstallRootSave.Clear();
        var draft = InstallRootDraft.Trim();
        if (draft.Length == 0)
        {
            InstallRootSave.SetFailure(Loc["settings_installRootRequired"]);
            return;
        }

        if (await _owner.UpdateInstallRootAsync(draft))
        {
            InstallRootSave.SetSuccess(Loc["settings_installRootSaved"]);
        }
        else
        {
            InstallRootSave.SetFailure(_owner.StatusMessage);
        }
    }

    /// <summary>可选主题列表（转发主窗口，显示名随语言重建）。</summary>
    public IReadOnlyList<ThemeOption> ThemeModes => _owner.ThemeModes;

    /// <summary>主题选择（直接读写主窗口状态并回传变更通知）。</summary>
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

    /// <summary>可选语言列表（显示名随当前语言重建）。</summary>
    public IReadOnlyList<LanguageOption> Languages => BuildLanguages();

    /// <summary>构建语言选项：跟随系统 + 简体中文 + English。</summary>
    private IReadOnlyList<LanguageOption> BuildLanguages() =>
    [
        new LanguageOption(LocalizationService.SystemLanguage, _owner.Loc["lang_system"]),
        new LanguageOption("zh-CN", _owner.Loc["lang_zh-CN"]),
        new LanguageOption("en-US", _owner.Loc["lang_en-US"]),
    ];

    /// <summary>当前语言；切换即全局生效并持久化，同时刷新选项列表显示名。</summary>
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

    /// <summary>返回游戏页（转发主窗口命令）。</summary>
    [RelayCommand]
    private void ShowGames() => _owner.ShowGamesCommand.Execute(null);

    /// <summary>应用自定义背景图路径显示（空白 = 内置渐变）。</summary>
    [ObservableProperty]
    private string _appBackgroundPath;

    /// <summary>应用背景操作结果提示。</summary>
    [ObservableProperty]
    private SaveMessageSlot _appBackgroundSave = new();

    /// <summary>挑一张图片设为应用背景，结果写入独立消息位。</summary>
    [RelayCommand]
    private async Task BrowseAppBackgroundAsync(CancellationToken cancellationToken)
    {
        AppBackgroundSave.Clear();
        var path = await _owner.PickImageFileAsync();
        if (path is null)
        {
            return;
        }

        if (await _owner.SetAppBackgroundAsync(path))
        {
            AppBackgroundPath = path;
            AppBackgroundSave.SetSuccess(Loc["settings_appBackground_saved"]);
        }
        else
        {
            AppBackgroundSave.SetFailure(Loc["settings_appBackground_failed"]);
        }
    }

    /// <summary>清除应用背景图恢复内置渐变，结果写入独立消息位。</summary>
    [RelayCommand]
    private async Task ResetAppBackgroundAsync()
    {
        AppBackgroundSave.Clear();
        if (await _owner.SetAppBackgroundAsync(null))
        {
            AppBackgroundPath = "";
            AppBackgroundSave.SetSuccess(Loc["settings_appBackground_resetDone"]);
        }
        else
        {
            AppBackgroundSave.SetFailure(Loc["settings_appBackground_failed"]);
        }
    }

    /// <summary>在系统文件管理器中打开配置文件所在目录（跨平台由系统 shell 决定）。</summary>
    [RelayCommand]
    private void OpenConfigFolder()
    {
        var directory = Path.GetDirectoryName(ConfigFilePath);
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        var open = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "explorer.exe"
                : OperatingSystem.IsMacOS() ? "open"
                : "xdg-open",
            Arguments = OperatingSystem.IsWindows() ? $"\"{directory}\"" : directory,
            UseShellExecute = true,
        };
        Process.Start(open);
    }
}

/// <summary>主题选项（枚举 + 界面显示名）。</summary>
public sealed record ThemeOption(ThemeMode Mode, string DisplayName);

/// <summary>语言选项（配置值 + 显示名；语言名本身不翻译）。</summary>
public sealed record LanguageOption(string Value, string DisplayName);

/// <summary>关于页的第三方组件条目。</summary>
public sealed record ThirdPartyItem(string Name, string License);

/// <summary>关于页：应用信息、版本与第三方声明。</summary>
public sealed partial class AboutViewModel(MainWindowViewModel owner) : ViewModelBase
{
    /// <summary>文案服务（转发主窗口实例，供 XAML 绑定）。</summary>
    public ILocalizationService Loc => owner.Loc;

    /// <summary>games.json 配置文件完整路径（展示用）。</summary>
    public string ConfigFilePath => owner.ConfigFilePath;

    /// <summary>应用程序集版本（AssemblyInformationalVersion 优先，含 git 信息时更长）。</summary>
    public string AppVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>运行时版本。</summary>
    public string RuntimeVersion => Environment.Version.ToString();

    /// <summary>第三方组件与许可声明。</summary>
    public IReadOnlyList<ThirdPartyItem> ThirdParty =>
    [
        new("Avalonia UI 12.1.2", "MIT"),
        new("CommunityToolkit.Mvvm 8.4.2", "MIT"),
        new(".NET 10 / Microsoft.Extensions.*", "MIT"),
        new("HDiffPatch (hpatchz)", "Apache-2.0"),
        new("ui-ux-pro-max design data", "MIT"),
        new("frontend-design skill", "Apache-2.0"),
    ];

    /// <summary>返回游戏页（转发主窗口命令）。</summary>
    [RelayCommand]
    private void ShowGames() => owner.ShowGamesCommand.Execute(null);
}
