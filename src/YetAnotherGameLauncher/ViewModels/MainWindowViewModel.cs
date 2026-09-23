using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Channels.Kuro;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Services.Umu;
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
    /// <summary>按游戏创建背景视频播放器的工厂（播放器随游戏页独占——切游戏互不干扰会话；
    /// null = 测试空跑，游戏 VM 拿到 null 播放器、静态海报兜底）。</summary>
    private readonly Func<IVideoBackdropPlayer>? _videoPlayerFactory;
    private readonly KuroGachaService? _gachaService;

    /// <summary>平台环境（打开目录等 OS 差异的抽象）。</summary>
    private readonly IPlatformInfo _platform;

    /// <summary>平台环境（供子 ViewModel 复用，测试可注入假实现）。</summary>
    internal IPlatformInfo Platform => _platform;
    private readonly NetworkProxyManager? _proxyManager;

    /// <summary>VM 实际持有的代理管理器；internal 供单测断言组合根装配（经 InternalsVisibleTo）。</summary>
    internal NetworkProxyManager? ProxyManager => _proxyManager;
    private readonly Func<string?>? _defaultConfigTemplateFactory;

    /// <summary>Linux 首运推荐模板用的 Proton 版本清单（null = 现场扫描；测试注入固定值保证确定性）。</summary>
    private readonly IReadOnlyList<string>? _linuxProtonVersions;

    /// <summary>Linux 首运推荐模板注入的 wine 路径与数据目录（null = 现场发现/默认；测试确定性用）。</summary>
    private readonly string? _linuxWinePath;
    private readonly string? _linuxDataHome;

    /// <summary>原生 umu 启动器（null = 测试/未注册）。</summary>
    private readonly NativeUmuLauncher? _nativeUmu;

    /// <summary>原生 umu 组件准备器（null = 测试/未注册）。</summary>
    private readonly IUmuComponentProvisioner? _umuProvisioner;

    /// <summary>持久化窗口状态（InitializeAsync 加载目录后可读；null = 未持久化过，窗口用 XAML 默认尺寸）。</summary>
    public int? PersistedWindowWidth => _catalogService.Catalog?.Settings.WindowWidth;

    /// <summary>持久化的窗口高度。</summary>
    public int? PersistedWindowHeight => _catalogService.Catalog?.Settings.WindowHeight;

    /// <summary>持久化的"关闭时是否最大化"。</summary>
    public bool PersistedWindowMaximized => _catalogService.Catalog?.Settings.WindowMaximized ?? false;

    /// <summary>
    /// 由 MainWindow 注入的"应用持久化窗口状态"回调（Width/Height/Maximized）：
    /// 目录加载完成后调用一次。窗口比目录先上屏（InitializeAsync 异步），无法在 XAML 阶段应用。
    /// </summary>
    public Action<double, double, bool>? WindowStateApplier { get; set; }

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
        IFilePickerService? filePicker = null,
        Func<IVideoBackdropPlayer>? videoPlayerFactory = null,
        KuroGachaService? gachaService = null,
        NetworkProxyManager? proxyManager = null,
        IPlatformInfo? platformInfo = null,
        IReadOnlyList<string>? linuxProtonVersions = null,
        string? linuxWinePath = null,
        string? linuxDataHome = null,
        NativeUmuLauncher? nativeUmu = null,
        IUmuComponentProvisioner? umuProvisioner = null)
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
        _videoPlayerFactory = videoPlayerFactory;
        _gachaService = gachaService;
        _proxyManager = proxyManager;
        _linuxProtonVersions = linuxProtonVersions;
        _linuxWinePath = linuxWinePath;
        _linuxDataHome = linuxDataHome;
        _nativeUmu = nativeUmu;
        _umuProvisioner = umuProvisioner;
        _platform = platformInfo ?? PlatformInfoFactory.Create();
        Loc = localization;
        _loc.PropertyChanged += OnLanguageChanged;
        RebuildThemeModes(keepMode: null);
        SelectedTheme = ThemeModes[0];
        CurrentPage = null;
    }

    /// <summary>暴露给 XAML 的文案服务：{Binding Loc[key]} 在语言切换时整体刷新。</summary>
    public ILocalizationService Loc { get; }

    /// <summary>游戏列表（按配置顺序构建；侧栏与导航的数据源）。</summary>
    public ObservableCollection<GameItemViewModel> Games { get; } = [];

    /// <summary>窗口是否最大化（最大化铺满屏幕时应去掉窗口顶部两角的圆角）。</summary>
    [ObservableProperty]
    private bool _isWindowMaximized;

    /// <summary>当前选中的游戏；变化时导航到详情页并刷新状态。</summary>
    [ObservableProperty]
    private GameItemViewModel? _selectedGame;

    /// <summary>游戏间切换的滑动方向暂存：Changing 钩子按列表顺序计算，Changed 钩子消费后复位。</summary>
    private bool _pendingGameNavBack;

    /// <summary>当前显示的页面（游戏详情/游戏设置/应用设置/关于）。</summary>
    [ObservableProperty]
    private object? _currentPage;

    /// <summary>应用名常量：窗口 Title 的回退值与游戏页标题的后缀。</summary>
    public const string AppTitle = "YetAnotherGameLauncher";

    /// <summary>窗口标题：游戏页为「游戏名 · 应用名」，其余页为应用名。
    /// 语言切换经 OnLanguageChanged 重建 CurrentPage 时本属性随之刷新（DisplayName 随文化变化）。</summary>
    public string WindowTitle => CurrentPage is GameItemViewModel game
        ? $"{game.DisplayName} · {AppTitle}"
        : AppTitle;

    /// <summary>导航方向：false=前进（新页自右滑入），true=后退（自左滑入），驱动页面切换动画。</summary>
    [ObservableProperty]
    private bool _isNavBack;

    /// <summary>侧栏高亮归属：当前页属于游戏（详情/游戏设置/唤取记录）。</summary>
    public bool IsGameNavActive => CurrentPage is GameItemViewModel or GameSettingsViewModel or GachaViewModel;

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
                // 停在设置/关于页时点击"仍是当前游戏"的列表项：不触发 SelectedGame 变化，
                // 这里按"返回游戏库"方向直接导航回去
                NavigateTo(value, back: true);
            }
        }
    }

    /// <summary>保活中的游戏详情页（切页时驱动播放/停止/暂停；切去非游戏页后仍指向该页的暂停态，
    /// 直到被另一游戏页替换——从非游戏页再切游戏时仍要驱动它的起停）。</summary>
    private GameItemViewModel? _activeVideoPage;

    /// <summary>暂停保活中的游戏页（按暂停先后排序，最新在尾）：超出上限淘汰最旧，防会话/帧位图无界驻留。</summary>
    private readonly List<GameItemViewModel> _parkedVideoPages = [];

    /// <summary>暂停保活上限：2 路暂停 + 1 路在播 = 最多 3 个解码会话与帧位图同时驻留
    /// （一路 ≈ 帧位图 13MB @2324×1392 + 打开的解码源；本启动器双游戏场景永不被淘汰）。</summary>
    internal const int MaxParkedVideoSessions = 2;

    /// <summary>页面切换时触发：转发通知给依赖 CurrentPage 的侧栏高亮与选中项绑定，并驱动背景视频起停。
    /// 播放器按游戏独占，切页（游戏页 ↔ 游戏页 / 游戏页 ↔ 非游戏页）一律暂停保活——解码泊车、
    /// 帧保留，重进即时续播；只有保活淘汰（超上限）/关窗/退出/列表重建才全停清场。</summary>
    partial void OnCurrentPageChanged(object? value)
    {
        var gamePage = value as GameItemViewModel;
        if (gamePage is not null && ReferenceEquals(_activeVideoPage, gamePage))
        {
            // 从非游戏页切回同一游戏页：续播保活会话（无会话则按已解析路径重新起播）
            _parkedVideoPages.Remove(gamePage);
            gamePage.SetDetailActive(true);
        }
        else if (!ReferenceEquals(_activeVideoPage, value))
        {
            var previous = _activeVideoPage;
            if (gamePage is not null)
            {
                previous?.SetDetailActive(false); // 游戏间切换：旧页同样保活（不再全停换源）
                _activeVideoPage = gamePage;
                _parkedVideoPages.Remove(gamePage);
                gamePage.SetDetailActive(true);
            }
            else if (previous is not null)
            {
                // 切到非游戏页：暂停保活，_activeVideoPage 保持指向该游戏页
                previous.SuspendVideo();
            }

            if (previous is not null && !ReferenceEquals(previous, gamePage))
            {
                TrackParkedVideo(previous);
            }
        }

        OnPropertyChanged(nameof(IsGameNavActive));
        OnPropertyChanged(nameof(IsSettingsNavActive));
        OnPropertyChanged(nameof(IsAboutNavActive));
        OnPropertyChanged(nameof(GameNavSelection));
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>登记一个刚被暂停保活的游戏页并执行上限淘汰：最旧的保活会话被全停清场
    /// （帧位图/解码源释放；该游戏重进时凭已解析路径重新起播自愈）。</summary>
    private void TrackParkedVideo(GameItemViewModel parked)
    {
        _parkedVideoPages.Remove(parked);
        _parkedVideoPages.Add(parked);
        while (_parkedVideoPages.Count > MaxParkedVideoSessions)
        {
            var evicted = _parkedVideoPages[0];
            _parkedVideoPages.RemoveAt(0);
            if (!ReferenceEquals(evicted, _activeVideoPage))
            {
                evicted.StopVideo();
            }
        }
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

    /// <summary>启动遮蔽是否在屏（App 启动路径经 <see cref="BeginBootSplash"/> 开启、
    /// <see cref="RunBootGateAsync"/> 放行撤下；默认 false——测试与截图导出不受影响）。</summary>
    [ObservableProperty]
    private bool _isBooting;

    /// <summary>启动门控放行超时（首启下载/慢网络兜底；测试注入缩小时长）。</summary>
    internal TimeSpan BootReadinessTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>启动遮蔽最小展示时长（测试注入缩小时长）。</summary>
    internal TimeSpan BootMinSplash { get; set; } = TimeSpan.FromSeconds(BootGate.MinSplashSeconds);

    /// <summary>静态海报放行宽限（测试注入缩小时长）：优先等视频首帧，海报只作宽限后的兜底。</summary>
    internal TimeSpan BootPosterGrace { get; set; } = TimeSpan.FromSeconds(BootGate.PosterGraceSeconds);

    /// <summary>开启启动遮蔽（App 启动路径在窗口上屏前调用一次）：初始化与背景预载在遮蔽后进行。</summary>
    public void BeginBootSplash() => IsBooting = true;

    /// <summary>
    /// 启动门控：遮蔽下有界轮询等待首个选中游戏的背景就绪——优先视频首帧（进入主界面即见
    /// 动画，无静态→动态跳变），静态海报仅在宽限期（<see cref="BootPosterGrace"/>）后兜底放行；
    /// 超时/配置错误/无游戏无条件放行。App 在 <see cref="InitializeAsync"/> 完成后调用；
    /// 未开启遮蔽（测试/重复调用）为空跑。纯决策见 <see cref="BootGate"/>。
    /// </summary>
    public async Task RunBootGateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBooting)
        {
            return;
        }

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var game = SelectedGame;
                if (BootGate.ShouldRelease(
                        videoReady: game?.HasBackgroundVideo == true,
                        posterReady: game?.HasBackgroundImage == true,
                        hasSelectedGame: game is not null,
                        configError: ConfigError,
                        elapsedSeconds: stopwatch.Elapsed.TotalSeconds,
                        timeoutSeconds: BootReadinessTimeout.TotalSeconds,
                        minSplashSeconds: BootMinSplash.TotalSeconds,
                        posterGraceSeconds: BootPosterGrace.TotalSeconds))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(BootGate.PollIntervalSeconds), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 应用退出：遮蔽随窗口一起消失
        }
        finally
        {
            IsBooting = false;
        }
    }

    /// <summary>右上角轻提示集合（瞬态信息：版本检测结果/检测到游戏、服务器切换/启动设置实际变更）；容量 3，过载丢弃最旧。</summary>
    public ObservableCollection<ToastItem> Toasts { get; } = [];

    /// <summary>弹出轻提示（UI 线程调用；toast 不排队等待，超容量直接丢最旧——状态胶囊承载全量状态）。
    /// 启动失败覆盖层在场时挂起新 toast（评审 P3-13：瞬态消息不得压过模态错误）。</summary>
    public void ShowToast(string title, string message, ToastKind kind)
    {
        if (CurrentPage is GameItemViewModel game && game.HasLaunchError)
        {
            return;
        }

        const int MaxToasts = 3;
        while (Toasts.Count >= MaxToasts)
        {
            // 先停被淘汰项的自灭计时器再移除：否则计时器白跑到点对已不在集合的项做空 Remove
            Toasts[0].StopAutoDismiss();
            Toasts.RemoveAt(0);
        }

        var toast = new ToastItem(title, message, kind, TimeSpan.FromSeconds(4), t => Toasts.Remove(t));
        Toasts.Add(toast);
        toast.StartAutoDismiss(); // 非 UI 线程（headless 测试直调）时静默跳过，由测试手动 Dismiss
    }

    /// <summary>当前状态提示是否为错误（驱动侧栏红字样式）。</summary>
    [ObservableProperty]
    private bool _configError;

    /// <summary>是否有待展示的状态提示（仅供同文件的状态样式派生使用）。</summary>
    private bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

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

    /// <summary>语言切换回调：重建主题/计数文案并刷新各游戏；重建当前页刷新构造期生成的内容。
    /// 视觉树重建只对绑定文本有效——唤取页的卡池名/游戏名是 VM 构造期快照，需换新 VM
    /// （记录列表从本地缓存自动重载）；启动设置卡的 LaunchModes 由其自身语言订阅重建。</summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        RebuildThemeModes(SelectedTheme?.Mode);
        OnPropertyChanged(nameof(GameCountText));
        foreach (var game in Games)
        {
            _ = game.RefreshAsync();
        }

        if (CurrentPage is GachaViewModel gacha && _gachaService is { } gachaService)
        {
            // 卡池名/游戏名等构造期快照随语言重建（此前注释宣称 CurrentPage=null 重建视觉树即可，
            // 实际 VM 原封不动、下拉选项维持旧语言——2026-09-20 复审修正机制）
            NavigateTo(new GachaViewModel(this, gacha.OwnerGame, gachaService));
            return;
        }

        // 兜底：强制重建当前页面的视觉树，刷新编译绑定重新求值的文本
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

    /// <summary>当前代理模式（设置页草稿初始化）。</summary>
    public ProxyMode ProxyMode => _catalogService.Catalog?.Settings.ProxyMode ?? ProxyMode.System;

    /// <summary>当前手动代理地址（设置页草稿初始化）。</summary>
    public string ProxyAddress => _catalogService.Catalog?.Settings.ProxyAddress ?? "";

    /// <summary>
    /// 应用代理设置：写回 games.json 持久化，并对共享 SocketsHttpHandler 热改代理（保存即时生效）。
    /// </summary>
    /// <param name="mode">代理模式。</param>
    /// <param name="address">手动代理地址（非 Manual 忽略）。</param>
    /// <returns>持久化是否成功——失败时调用方不得弹"已保存"（重启后设置回退，2026-09-20 复审修复）。</returns>
    public async Task<bool> ApplyProxySettingsAsync(ProxyMode mode, string address)
    {
        if (_catalogService.Catalog is not { } catalog)
        {
            return false;
        }

        catalog.Settings.ProxyMode = mode;
        // 地址仅在"自定义代理"下有意义：其他选择清空持久化值，避免残留地址误导
        catalog.Settings.ProxyAddress = mode == ProxyMode.Manual && !string.IsNullOrWhiteSpace(address)
            ? address
            : null;
        _proxyManager?.Apply(catalog.Settings);
        return await TrySaveCatalogAsync();
    }

    /// <summary>侧栏游戏计数文案（随语言切换刷新）。</summary>
    public string GameCountText => _loc.Format("sidebar_games_count", Games.Count);

    /// <summary>侧栏展开宽度（宽度驱动侧栏过渡动画）。</summary>
    private const double SidebarExpandedWidth = 264;

    /// <summary>侧栏收起时的窄条宽度（68px 图标条）。</summary>
    private const double SidebarCollapsedWidth = 68;

    /// <summary>窗口宽度阈值（滞回）：低于下限自动收起侧栏、高于上限自动展开，区间内保持现状。</summary>
    internal const double SidebarCollapseThreshold = 1000;
    internal const double SidebarExpandThreshold = 1080;

    /// <summary>
    /// 窗口尺寸变化（MainWindow.SizeChanged 转发）：穿越阈值时自动切换侧栏展开态，
    /// 宽度过渡走现有 0.2s 动画；手动收放仍可用，阈值再次穿越时以窗口宽度为准。
    /// </summary>
    public void SetWindowWidth(double width)
    {
        if (width < SidebarCollapseThreshold && IsSidebarExpanded)
        {
            IsSidebarExpanded = false;
        }
        else if (width > SidebarExpandThreshold && !IsSidebarExpanded)
        {
            IsSidebarExpanded = true;
        }
    }

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

    /// <summary>
    /// 选中游戏变化前按列表顺序计算滑动方向：向下切（索引变大）= 前进（自右滑入），
    /// 向上切 = 后退（自左滑入）；初始选中/重建列表（旧值为空）走 Changed 里的默认前进。
    /// </summary>
    partial void OnSelectedGameChanging(GameItemViewModel? oldValue, GameItemViewModel? newValue)
    {
        if (oldValue is not null && newValue is not null)
        {
            _pendingGameNavBack = Games.IndexOf(newValue) < Games.IndexOf(oldValue);
        }
    }

    /// <summary>选中游戏变化时触发：转发侧栏选中通知，非空时导航到详情页并刷新状态。</summary>
    partial void OnSelectedGameChanged(GameItemViewModel? value)
    {
        OnPropertyChanged(nameof(GameNavSelection));
        if (value is not null)
        {
            NavigateTo(value, back: _pendingGameNavBack);
            _pendingGameNavBack = false;
            _ = value.RefreshAsync();
        }

    }

    /// <summary>启动时初始化：加载配置 → 应用语言/主题 → 构建游戏列表。失败时给出可读提示而不崩溃。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        StatusMessage = "";
        ConfigError = false;
        var catalog = new GameCatalog();
        var loaded = false;
        try
        {
            await _catalogService.LoadAsync(cancellationToken);
            catalog = _catalogService.Catalog!;
            ConfigError = false;
            loaded = true;
        }
        catch (FileNotFoundException)
        {
            // 首次运行：在默认位置生成默认配置文件（优先使用随应用分发的模板），随后加载。
            // 生成/读取中途的失败（配置路径被占用成目录、权限等）不能逃出本方法——catch 块内
            // 抛出的异常不会被同级过滤器接住，需内层再兜；同样只提示、不写盘
            try
            {
                await _catalogService.CreateDefaultFileAsync(_defaultConfigTemplateFactory?.Invoke(), cancellationToken);
                await _catalogService.LoadAsync(cancellationToken);
                catalog = _catalogService.Catalog!;
                ConfigError = false;
                loaded = true;
                StatusMessage = _loc.Format("message_configCreated", ConfigFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GameCatalogValidationException)
            {
                ConfigError = true;
                StatusMessage = ex is GameCatalogValidationException
                    ? ex.Message
                    : _loc.Format("message_configReadFailed", ex.Message);
            }

            if (loaded)
            {
                // Linux 首运默认模板升级失败不致命（下次启动重试，与迁移的兜底一致）；
                // 不置 ConfigError——文件已建成且有效，误报"读取失败"会误导用户。
                // 不能并入上面的内层 try：那会把升级写盘失败误报成读取失败
                try
                {
                    await ApplyLinuxFirstRunLaunchDefaultsAsync(catalog, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (GameCatalogValidationException ex)
        {
            // 手改 games.json 是受支持的工作流：校验失败只提示、绝不写盘。
            // Catalog 必须保持 null：PersistWindowState 与一切设置保存路径都以
            // "Catalog is null 静默跳过"为最后防线——若把空目录装回去，用户看完错误
            // 提示随手关窗就会把 games.json 覆盖成空配置（数据丢失，实测复现过）
            ConfigError = true;
            StatusMessage = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 配置不可读/不可写（占用、权限、磁盘故障）：同样只提示、不写盘、Catalog 保持 null
            ConfigError = true;
            StatusMessage = _loc.Format("message_configReadFailed", ex.Message);
        }

        // 迁移会无条件 SaveAsync：只有本次确实加载成功才允许改写用户配置，
        // 失败分支保持文件原样等用户修复后重进
        if (loaded)
        {
            await MigrateFromSampleAsync(catalog, cancellationToken);
            await MigrateLinuxBareLaunchTemplatesAsync(catalog, cancellationToken);
            await MigrateLinuxLegacyUmuTemplatesAsync(catalog, cancellationToken);
        }

        _downloader.Limiter.BytesPerSecond = catalog.Settings.DownloadSpeedLimitBytes;
        _proxyManager?.Apply(catalog.Settings);

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
        WarmupGames();
        OnPropertyChanged(nameof(GameCountText));
        OnPropertyChanged(nameof(InstallRoot));

        // 目录就绪后应用持久化的窗口状态（窗口先于目录上屏，只能在此时补应用）
        if (PersistedWindowWidth is { } width && PersistedWindowHeight is { } height)
        {
            WindowStateApplier?.Invoke(width, height, PersistedWindowMaximized);
        }
    }

    /// <summary>
    /// 停止全部背景视频播放（窗口关闭/应用退出时调用）。播放器按游戏独占，必须逐个停掉——
    /// 含暂停保活中的会话。必须先于窗口销毁执行：退出期平台拆除会弄坏 GPU 解码栈
    /// （VAAPI/NVDEC 全部初始化失败），解码循环若继续运行会以每帧两条的速度向 stderr 刷
    /// 硬件解码失败。静默：视频仅是装饰，失败不影响退出。
    /// </summary>
    public void StopBackdropVideo()
    {
        _parkedVideoPages.Clear();
        foreach (var game in Games)
        {
            game.StopVideo();
        }
    }

    /// <summary>
    /// 窗口关闭时把当前尺寸/最大化状态写回配置（Closing 是同步事件，JSON 很小，
    /// 同步等待落盘保证进程退出前写完）。目录未加载（配置损坏）时静默跳过。
    /// </summary>
    public void PersistWindowState(double width, double height, bool maximized)
    {
        if (_catalogService.Catalog is not { } catalog)
        {
            return;
        }

        catalog.Settings.WindowWidth = (int)Math.Round(width);
        catalog.Settings.WindowHeight = (int)Math.Round(height);
        catalog.Settings.WindowMaximized = maximized;
        try
        {
            // 同步阻塞的安全性前提（缺一即死锁/卡 UI，改动前必读）：
            // 1. Core 层 .editorconfig 强制 CA2007——SaveAsync 全链 ConfigureAwait(false)，
            //    无 UI 线程续体，GetResult() 不会死锁；
            // 2. 本方法仅窗口 Closing 路径调用（无法 async），阻塞时长为一次小文件原子写。
            _catalogService.SaveAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 退出路径：写失败不影响关闭（下次启动沿用旧值）
        }
    }

    /// <summary>按当前配置重建游戏列表（installRoot 变更后调用），返回未知渠道提示列表。</summary>
    private List<string> RebuildGames(GameCatalog catalog)
    {
        // 旧列表整体废弃：先退订懒创建子 VM（LaunchSettings）对单例服务的事件订阅，
        // 否则旧 VM 链被单例委托钉住无法回收（2026-09-20 复审结构性消除）；
        // 播放器按游戏独占后，旧 VM 的会话（含暂停保活中的）也须一并全停释放
        foreach (var game in Games)
        {
            game.DetachEventSubscriptions();
            game.StopVideo();
        }

        _parkedVideoPages.Clear();
        _activeVideoPage = null;
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
                _backdropService,
                _videoPlayerFactory?.Invoke(),
                _filePicker,
                _platform,
                _nativeUmu,
                _umuProvisioner));

            // 游戏状态变化中的瞬态信息（检测到游戏/有更新/可预下载）经事件转发为右上角轻提示
            var added = Games[^1];
            added.StatusToastRequested += (title, message, kind) => ShowToast(title, message, kind);
            // 设置类变化（服务器切换/启动设置实际变更落盘）同样转发为轻提示
            added.SettingsToastRequested += (title, message, kind) => ShowToast(title, message, kind);
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

    /// <summary>弹系统目录选择对话框（起始位置为建议目录）；未注册选择器（测试）时返回 null。</summary>
    public async Task<string?> PickFolderAsync(string title, string? suggestedPath)
    {
        if (_filePicker is null)
        {
            return null;
        }

        return await _filePicker.PickFolderAsync(title, suggestedPath);
    }

    /// <summary>应用下载限速（字节/秒）并持久化；0 = 不限速。返回持久化是否成功。</summary>
    public async Task<bool> ApplySpeedLimitAsync(long bytesPerSecond, CancellationToken cancellationToken = default)
    {
        _downloader.Limiter.BytesPerSecond = bytesPerSecond;
        if (_catalogService.Catalog is { } catalog)
        {
            catalog.Settings.DownloadSpeedLimitBytes = bytesPerSecond;
            return await TrySaveCatalogAsync(cancellationToken);
        }

        return false;
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
        WarmupGames();
        OnPropertyChanged(nameof(InstallRoot));
        return true;
    }

    /// <summary>
    /// 游戏列表就绪后的预热（启动与安装根目录变更重建列表后调用）：非选中游戏并行预加载
    /// 图标与背景（仅读磁盘缓存，零网络）并做各自的一次版本/预载检测。
    /// 选中游戏跳过——其完整刷新（含资产加载与版本检测）已由 SelectedGame 赋值触发，重复跑纯属浪费。
    /// </summary>
    private void WarmupGames()
    {
        foreach (var game in Games)
        {
            if (ReferenceEquals(game, SelectedGame))
            {
                continue;
            }

            _ = game.PreloadAssetsAsync();
            _ = game.RefreshAsync();
        }
    }

    /// <summary>
    /// Linux 首运兜底：默认模板的裸 {exe} 无法运行 Windows 客户端（Exec format error），
    /// 物化配置后立即把这类模板升级为社区推荐链（umu → Proton → wine；什么都不装也给 umu 模板，
    /// 引导安装完成后即可启动）并落盘。仅在首运创建时触发一次，用户此后的任何修改不再被触碰。
    /// </summary>
    private async Task ApplyLinuxFirstRunLaunchDefaultsAsync(GameCatalog catalog, CancellationToken cancellationToken)
    {
        if (!_platform.IsLinux || catalog.Games.Count == 0)
        {
            return;
        }

        if (UpgradeTemplatesToRecommended(catalog))
        {
            await _catalogService.SaveAsync(cancellationToken);
        }
    }

    /// <summary>
    /// schemaVersion 4 一次性迁移：首运升级功能上线**之前**物化的存量配置仍是裸 {exe}，
    /// Linux 上同样批量升级为推荐链（判定条件与首运兜底一致：仅裸 {exe}，自定义模板不动）。
    /// 版本号 ≥ 4 后永不执行；Windows 仅推进版本号作迁移标记，不动模板。
    /// </summary>
    private async Task MigrateLinuxBareLaunchTemplatesAsync(GameCatalog catalog, CancellationToken cancellationToken)
    {
        if (catalog.Settings.SchemaVersion >= 4)
        {
            return;
        }

        catalog.Settings.SchemaVersion = 4;
        if (_platform.IsLinux && UpgradeTemplatesToRecommended(catalog))
        {
            StatusMessage = _loc["message_launchMigrated"];
        }

        try
        {
            await _catalogService.SaveAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 迁移写回失败不致命（与 schemaVersion 3 迁移同款）：版本号未落盘，下次启动幂等重试；
            // 裸抛会让整个初始化中途夭折——空窗口无提示（2026-09-20 三审修复）
        }
    }

    /// <summary>
    /// schemaVersion 5 一次性迁移：schemaVersion 4 只升级裸 {exe}，存量外部 umu 模板
    /// （历史 BuildUmuLaunch 在 umu-run 未发现时落盘的裸命令名 "umu-run {exe}"）被跳过——
    /// 外部 umu-launcher 已整体移除，这类模板一律升级为推荐链（原生 umu）。
    /// 版本号 ≥ 5 后永不执行；Windows 仅推进版本号作迁移标记，不动模板。
    /// </summary>
    private async Task MigrateLinuxLegacyUmuTemplatesAsync(GameCatalog catalog, CancellationToken cancellationToken)
    {
        if (catalog.Settings.SchemaVersion >= 5)
        {
            return;
        }

        catalog.Settings.SchemaVersion = 5;
        if (_platform.IsLinux && UpgradeTemplatesToRecommended(catalog, includeLegacyUmuTemplates: true))
        {
            StatusMessage = _loc["message_launchMigrated"];
        }

        try
        {
            await _catalogService.SaveAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 迁移写回失败不致命（与 schemaVersion 3 迁移同款）：版本号未落盘，下次启动幂等重试
        }
    }

    /// <summary>
    /// 把目录内的存量模板升级为推荐链；有改动返回 true（版本清单单一来源：BuildRecommendedLaunch）。
    /// 升级范围：裸 {exe} 一律升级；includeLegacyUmuTemplates 时加上 "umu-run {exe}"
    /// （外部 umu-launcher 已移除，这类历史自动生成的模板一律升级）。其余自定义模板完全不动。
    /// </summary>
    private bool UpgradeTemplatesToRecommended(GameCatalog catalog, bool includeLegacyUmuTemplates = false)
    {
        var versions = _linuxProtonVersions ?? CompatTools.FindProtonVersions();
        var winePath = _linuxWinePath ?? CompatTools.FindSystemWine();
        var dataHome = _linuxDataHome ?? AppPaths.DataHomeDirectory; // CompatTools 语义要求数据根（不含 yagl 后缀）
        var changed = false;
        foreach (var game in catalog.Games)
        {
            var template = game.Launch.CommandTemplate.Trim();
            var isBare = string.Equals(template, "{exe}", StringComparison.Ordinal);
            // 存量外部 umu 模板（历史 BuildUmuLaunch 落盘形态）属应用生成而非用户手写，统一升级；
            // 用户手写的任何其它模板（含自备的 umu-run / wine / Proton）完全不触碰
            var isLegacyUmu = includeLegacyUmuTemplates
                && string.Equals(template, "umu-run {exe}", StringComparison.OrdinalIgnoreCase);
            if (!isBare && !isLegacyUmu)
            {
                continue; // 用户已有自定义模板：完全不动
            }

            var launch = CompatTools.BuildRecommendedLaunch(
                game.Id, versions, _platform.IsNvidiaGpuPresent,
                dataHome: dataHome, winePath: winePath);
            game.Launch.CommandTemplate = launch.CommandTemplate;
            foreach (var (key, value) in launch.Environment)
            {
                game.Launch.Environment[key] = value;
            }

            changed = true;
        }

        return changed;
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

    /// <summary>进入鸣潮唤取记录页（详情页入口；kuro 渠道专属功能）。</summary>
    /// <param name="game">来源游戏（当前仅鸣潮）。</param>
    [RelayCommand]
    private void ShowGacha(GameItemViewModel game)
    {
        if (_gachaService is not null)
        {
            NavigateTo(new GachaViewModel(this, game, _gachaService));
        }
    }
}

/// <summary>设置页：外观（主题/语言）、配置文件与下载设置（其余编辑走 games.json）。</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _owner;

    /// <summary>radio 互斥协调的防重入守卫（级联清空其他项时不再反向触发）。</summary>
    private bool _syncingProxyRadios;

    public SettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        _installRootDraft = owner.InstallRoot;
        _speedLimitMbDraft = FormatSpeed(owner.DownloadSpeedLimitBytes);
        _appBackgroundPath = owner.ConfiguredAppBackground;

        // 地址草稿仅在"自定义代理"下回显历史值：其他选择下显示空框，
        // 避免出现"填了值却禁用"的误导观感（配置本体不受影响，切回自定义即恢复）
        _proxyAddressDraft = owner.ProxyMode == ProxyMode.Manual ? owner.ProxyAddress : "";
        ApplyModeToRadios(owner.ProxyMode);
    }

    /// <summary>把代理选择映射到三个 radio（构造与回显用，守卫避免级联）。</summary>
    private void ApplyModeToRadios(ProxyMode mode)
    {
        _syncingProxyRadios = true;
        ProxyFollowSystem = mode == ProxyMode.System;
        ProxyDirect = mode == ProxyMode.None;
        ProxyManual = mode == ProxyMode.Manual;
        _syncingProxyRadios = false;
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

    /// <summary>代理选择：跟随系统代理（默认）。</summary>
    [ObservableProperty]
    private bool _proxyFollowSystem;

    /// <summary>代理选择：直连（不使用代理）。</summary>
    [ObservableProperty]
    private bool _proxyDirect;

    /// <summary>代理选择：使用自定义代理。</summary>
    [ObservableProperty]
    private bool _proxyManual;

    /// <summary>自定义代理地址草稿。</summary>
    [ObservableProperty]
    private string _proxyAddressDraft = "";

    /// <summary>代理保存结果提示。</summary>
    [ObservableProperty]
    private SaveMessageSlot _proxySave = new();

    /// <summary>地址框仅在"使用自定义代理"下可编辑。</summary>
    public bool IsProxyAddressEnabled => ProxyManual;

    partial void OnProxyFollowSystemChanged(bool value) => SyncProxyRadios(ProxyMode.System, value);
    partial void OnProxyDirectChanged(bool value) => SyncProxyRadios(ProxyMode.None, value);
    partial void OnProxyManualChanged(bool value)
    {
        SyncProxyRadios(ProxyMode.Manual, value);
        // 切入"自定义"时回显已保存的地址（草稿为空才填，不打断正在输入的内容）
        if (value && ProxyAddressDraft.Length == 0)
        {
            _proxyAddressDraft = _owner.ProxyAddress ?? "";
            OnPropertyChanged(nameof(ProxyAddressDraft));
        }
    }

    /// <summary>勾选某项时清空另外两项（radio 语义）；取消勾选不做级联。</summary>
    private void SyncProxyRadios(ProxyMode selected, bool value)
    {
        if (_syncingProxyRadios || !value)
        {
            return;
        }

        _syncingProxyRadios = true;
        if (selected != ProxyMode.System)
        {
            ProxyFollowSystem = false;
        }

        if (selected != ProxyMode.None)
        {
            ProxyDirect = false;
        }

        if (selected != ProxyMode.Manual)
        {
            ProxyManual = false;
        }

        _syncingProxyRadios = false;
        ProxySave.Clear();
        OnPropertyChanged(nameof(IsProxyAddressEnabled));
    }

    partial void OnProxyAddressDraftChanged(string value) => ProxySave.Clear();

    /// <summary>当前 radio 对应的代理选择。</summary>
    private ProxyMode SelectedMode => ProxyManual ? ProxyMode.Manual
        : ProxyDirect ? ProxyMode.None : ProxyMode.System;

    /// <summary>应用代理草稿：写回设置、即时生效（共享 handler 热改），并持久化。</summary>
    [RelayCommand]
    private async Task SaveProxyAsync(CancellationToken cancellationToken)
    {
        ProxySave.Clear();
        var mode = SelectedMode;
        var address = ProxyAddressDraft.Trim();
        var proxyValid = mode != ProxyMode.Manual
            || (Uri.TryCreate(address, UriKind.Absolute, out var proxy) && proxy.Scheme is "http" or "https");
        if (!proxyValid)
        {
            ProxySave.SetFailure(Loc["settings_proxyInvalid"]);
            return;
        }

        var saved = await _owner.ApplyProxySettingsAsync(mode, address);
        if (saved)
        {
            ProxySave.SetSuccess(Loc["settings_proxySaved"]);
        }
        else
        {
            // 持久化失败不得弹"已保存"：内存已生效但重启回退，双通道须一致（2026-09-20 复审修复）
            ProxySave.SetFailure(Loc.Format("message_saveFailed", Loc["message_saveFailedGeneric"]));
        }
    }

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

        var saved = await _owner.ApplySpeedLimitAsync((long)Math.Round(mb * 1024 * 1024), cancellationToken);
        if (saved)
        {
            SpeedLimitSave.SetSuccess(Loc["settings_downloadLimitSaved"]);
        }
        else
        {
            SpeedLimitSave.SetFailure(Loc.Format("message_saveFailed", Loc["message_saveFailedGeneric"]));
        }
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

    /// <summary>
    /// 弹系统目录选择对话框选安装根目录：选中即写入草稿并保存（替代原"保存"按钮），取消则不动草稿。
    /// 未注册选择器（无头测试/服务缺失）时命令无副作用。
    /// </summary>
    [RelayCommand]
    private async Task BrowseInstallRootAsync(CancellationToken cancellationToken)
    {
        InstallRootSave.Clear();
        var path = await _owner.PickFolderAsync(Loc["settings_installRootPickTitle"], InstallRootDraft);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        InstallRootDraft = LaunchSettingsViewModel.NormalizeDirectoryPath(path);
        await SaveInstallRootAsync(cancellationToken);
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

        _owner.Platform.OpenDirectoryInFileManager(directory);
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

    /// <summary>应用程序集版本（取 AssemblyName.Version 的主/次/补丁三段）。</summary>
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
        new("FFmpeg (libavcodec/libavformat/libswscale)", "LGPL-2.1+"),
        new("FFmpeg.AutoGen", "LGPL-2.1+"),
        new("SharpCompress", "MIT"),
    ];

    /// <summary>返回游戏页（转发主窗口命令）。</summary>
    [RelayCommand]
    private void ShowGames() => owner.ShowGamesCommand.Execute(null);

    /// <summary>项目主页（评审 P3-15：README 有仓库地址而 UI 无出口）。</summary>
    public const string ProjectHomeUrl = "https://github.com/AngelBeats-Kanade/YetAnotherGameLauncher";

    /// <summary>打开项目主页（经平台缝调系统浏览器；测试注入假平台记录调用）。</summary>
    [RelayCommand]
    private void OpenProjectHome() => owner.Platform.OpenInBrowser(ProjectHomeUrl);
}
