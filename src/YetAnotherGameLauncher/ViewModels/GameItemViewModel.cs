using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>侧边栏中的一个游戏：状态刷新与全部操作（启动/安装/更新/预下载）。</summary>
public partial class GameItemViewModel(
    GameDefinition game,
    string installDir,
    IGameChannelApi channel,
    GameUpdateService updateService,
    GameLauncherService launcherService,
    ILocalizationService localizationService,
    GameCatalogService catalogService,
    BackgroundImageService backgroundImageService,
    GameBackdropService backdropService,
    IVideoBackdropPlayer? videoPlayer = null,
    IFilePickerService? filePicker = null) : ViewModelBase
{
    private string _installDir = installDir;

    private readonly IFilePickerService? _filePicker = filePicker;

    /// <summary>背景视频播放器（单例共享；null = 测试场景或平台无解码能力）。</summary>
    public IVideoBackdropPlayer? VideoPlayer { get; } = videoPlayer;

    private LaunchSettingsViewModel? _launchSettings;

    /// <summary>启动设置编辑卡（保存走 GameCatalogService 整文件原子写）。</summary>
    public LaunchSettingsViewModel LaunchSettings => _launchSettings ??= new(
        Game, _installDir, catalogService, Loc, this, _filePicker);

    /// <summary>底层游戏配置（只读引用；名称/图标/服务器等以此为准）。</summary>
    public GameDefinition Game { get; } = game;

    /// <summary>暴露给 XAML 的文案服务（详情页模板绑定 {Binding Loc[key]}）。</summary>
    public ILocalizationService Loc { get; } = localizationService;

    /// <summary>显示名：配置 nameLocalized 按当前语言取值，缺失回退 displayName。</summary>
    public string DisplayName => ResolveDisplayName();

    /// <summary>列表图标：显示名首字（icon 加载失败/未配置时的回退）。</summary>
    public string IconText => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1];

    private string ResolveDisplayName()
    {
        var culture = Loc.EffectiveCulture;
        if (Game.NameLocalized.TryGetValue(culture, out var exact))
        {
            return exact;
        }

        // 语言前缀回退：zh-TW → zh-CN 等同前缀项
        var prefix = culture.Split('-')[0];
        foreach (var (key, value) in Game.NameLocalized)
        {
            if (key.Split('-')[0].Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return Game.DisplayName;
    }

    /// <summary>官方游戏图标（games.json 的 icon 字段；异步加载，失败回退首字）。</summary>
    [ObservableProperty]
    private IImage? _gameIcon;

    /// <summary>官方图标是否加载成功（失败时列表回退首字贴片）。</summary>
    [ObservableProperty]
    private bool _hasGameIcon;

    /// <summary>可选服务器列表（来自配置，构造时快照）。</summary>
    public ObservableCollection<GameServer> Servers { get; } = [.. game.Servers];

    /// <summary>当前选中服务器（默认第一个；版本查询与安装/更新均针对它）。</summary>
    [ObservableProperty]
    private GameServer _selectedServer = game.Servers.Count > 0 ? game.Servers[0] : new GameServer { Id = "", Name = "" };

    /// <summary>状态栏提示（连接失败/未安装/可更新等）。</summary>
    [ObservableProperty] private string _statusText = "";
    /// <summary>版本展示文案（本地/最新/可更新对照）。</summary>
    [ObservableProperty] private string _versionText = "";
    /// <summary>当前服务器是否已安装。</summary>
    [ObservableProperty] private bool _isInstalled;
    /// <summary>是否满足启动条件（已安装且可执行文件存在）。</summary>
    [ObservableProperty] private bool _canLaunch;
    /// <summary>本地版本落后于远端最新版。</summary>
    [ObservableProperty] private bool _hasUpdate;
    /// <summary>渠道提供预下载且尚未暂存。</summary>
    [ObservableProperty] private bool _predownloadAvailable;
    /// <summary>已有暂存的预下载包待应用。</summary>
    [ObservableProperty] private bool _hasStagedPredownload;
    /// <summary>是否有操作进行中（启动/安装/更新互斥）。</summary>
    [ObservableProperty] private bool _isBusy;
    /// <summary>当前操作进度百分比（0-100）。</summary>
    [ObservableProperty] private double _progressPercent;
    /// <summary>当前操作进度文案（下载/校验/打补丁等阶段）。</summary>
    [ObservableProperty] private string _progressText = "";

    /// <summary>详情页背景图（异步加载；null = 回退主题渐变）。</summary>
    [ObservableProperty]
    private IImage? _backgroundImage;

    /// <summary>详情页背景图是否加载成功（失败回退主题渐变）。</summary>
    [ObservableProperty]
    private bool _hasBackgroundImage;

    /// <summary>背景视频首帧是否已就绪（就绪后视频层盖过静态海报淡入接管）。</summary>
    [ObservableProperty]
    private bool _hasBackgroundVideo;

    /// <summary>已解析到、待播放（或播放中）的视频来源；null = 无视频背景。</summary>
    private string? _pendingVideoPath;

    /// <summary>详情页是否可见（切页驱动；控制视频只在页面上播放）。</summary>
    private bool _detailActive;

    /// <summary>播放器帧通知订阅状态（避免重复订阅）。</summary>
    private bool _videoSubscribed;

    /// <summary>主操作按钮文案：未安装→安装，有更新→更新，否则校验。</summary>
    public string InstallButtonText => !IsInstalled ? Loc["game_install"] : HasUpdate ? Loc["game_update"] : Loc["game_verify"];

    /// <summary>渠道显示名（已知渠道给中文名，未知原样）。</summary>
    public string ChannelDisplayName => Game.Channel switch
    {
        "kuro" => "Kuro Games",
        "hypergryph" => "GRYPHLINE",
        var other => other,
    };

    /// <summary>当前生效的安装目录（绝对路径；设置卡保存后就地更新）。</summary>
    public string InstallDirPath => _installDir;

    /// <summary>服务器数量文案（如"服务器：2"）。</summary>
    public string ServerCountText => Loc.Format("game_info_servers_count", Servers.Count);

    /// <summary>刷新安装状态/版本/预下载可用性（语言或渠道数据变化后也会调用）。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // 语言可能已切换：显示名/图标首字随语言重建
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IconText));

        // 背景为装饰性资源：后台加载，不阻塞状态刷新（否则 StatusText 等会晚到）
        _ = LoadBackgroundImageAsync(cancellationToken);

        var state = new LocalStateService(_installDir).Load(Game.Id, SelectedServer.Id);
        var staged = IncrementalUpdateService.TryLoadStagedManifest(_installDir);
        HasStagedPredownload = staged is not null;

        ChannelVersionInfo info;
        try
        {
            info = await channel.GetVersionInfoAsync(SelectedServer, cancellationToken);
            StatusText = "";
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or TaskCanceledException)
        {
            StatusText = Loc["status_noConnection"];
            VersionText = state is null ? Loc["status_notInstalledShort"] : Loc.Format("status_localVersion", state.Version);
            IsInstalled = state is not null;
            HasUpdate = false;
            PredownloadAvailable = false;
            CanLaunch = IsInstalled && ExecutableExists();
            OnPropertyChanged(nameof(InstallButtonText));
            return;
        }

        IsInstalled = state is not null;
        CanLaunch = IsInstalled && ExecutableExists();
        HasUpdate = VersionComparison.IsNewer(info.LatestVersion, state?.Version);
        PredownloadAvailable = info.PredownloadAvailable && !HasStagedPredownload;

        VersionText = state is null
            ? Loc.Format("version_latest", info.LatestVersion)
            : HasUpdate
                ? Loc.Format("version_canUpdate", state.Version, info.LatestVersion)
                : Loc.Format("version_local", state.Version);

        StatusText = !IsInstalled
            ? Loc["status_notInstalled"]
            : HasUpdate ? Loc["status_hasUpdate"] : Loc["status_upToDate"];

        OnPropertyChanged(nameof(InstallButtonText));
    }

    private async Task LoadBackgroundImageAsync(CancellationToken cancellationToken)
    {
        // 图标与背景分别兜底：背景链路失败不应吞掉图标（图标失败同样回退首字贴片）
        try
        {
            var icon = await backgroundImageService.LoadAsync(Game.Icon, cancellationToken);
            GameIcon = icon;
            HasGameIcon = icon is not null;
        }
        catch (Exception)
        {
            // 装饰性资源失败不影响功能
        }

        try
        {
            // 背景来源（配置文件不携带背景地址，每次打开都向渠道确认当期背景）：
            // 渠道背景服务（远程接口/官方启动器缓存，含磁盘缓存）→ null 时回退主题渐变。
            // 视频背景：先上海报（官方首帧图/静态兜底），首帧解码到达后视频层再接管
            var region = RegionForLanguage(Loc.EffectiveCulture);
            var backdrop = await backdropService.ResolveAsync(
                new BackdropRequest(Game.Id, Game.Channel, region, _installDir, SelectServerOptions(region)),
                cancellationToken);

            if (backdrop?.Kind == BackdropKind.Video && backdrop.Source is { } videoPath)
            {
                var poster = await backgroundImageService.LoadAsync(backdrop.PosterSource, cancellationToken);
                BackgroundImage = poster;
                HasBackgroundImage = poster is not null;

                if (_detailActive)
                {
                    _ = StartVideoAsync(videoPath);
                }
                else
                {
                    _pendingVideoPath = videoPath;
                }
            }
            else
            {
                StopVideo();
                var image = await backgroundImageService.LoadAsync(backdrop?.Source, cancellationToken);
                BackgroundImage = image;
                HasBackgroundImage = image is not null;
            }
        }
        catch (Exception)
        {
            // 与 BackgroundImageService 的静默回退一致：装饰性资源失败不影响功能
        }
    }

    /// <summary>详情页可见性变化（MainWindowViewModel 切页驱动）：进页起播待播视频，离页停止。</summary>
    internal void SetDetailActive(bool active)
    {
        _detailActive = active;
        if (!active)
        {
            StopVideo();
            return;
        }

        if (_pendingVideoPath is { } pending)
        {
            _ = StartVideoAsync(pending);
        }
    }

    /// <summary>起播背景视频：订阅帧通知后交给播放器（后台起播，失败保持静态海报）。</summary>
    private async Task StartVideoAsync(string videoPath)
    {
        if (VideoPlayer is null)
        {
            return;
        }

        StopVideo();
        _pendingVideoPath = videoPath;
        VideoPlayer.FrameUpdated += OnVideoFrameUpdated;
        _videoSubscribed = true;

        if (!await VideoPlayer.PlayAsync(videoPath))
        {
            StopVideo();
        }
    }

    /// <summary>停止视频播放：退订通知、隐藏视频层（静态海报/渐变兜底立即显示）。</summary>
    private void StopVideo()
    {
        _pendingVideoPath = null;
        if (_videoSubscribed && VideoPlayer is not null)
        {
            VideoPlayer.FrameUpdated -= OnVideoFrameUpdated;
            _videoSubscribed = false;
        }

        VideoPlayer?.Stop();
        HasBackgroundVideo = false;
    }

    /// <summary>播放器帧就绪：首帧到达后隐藏海报、显示视频层（幂等，重设同值不触发通知）。</summary>
    private void OnVideoFrameUpdated(object? sender, EventArgs e) => HasBackgroundVideo = true;

    /// <summary>界面语言决定背景区域：中文走国服渠道，其余走国际服渠道。</summary>
    private static string RegionForLanguage(string culture) =>
        culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "cn" : "global";

    /// <summary>按区域挑服务器：cn → id 为 cn 的服务器；global → global，避免误用 B 服端点。</summary>
    private IReadOnlyDictionary<string, string> SelectServerOptions(string region)
    {
        var preferred = Servers.FirstOrDefault(s => s.Id.Equals(region, StringComparison.OrdinalIgnoreCase))
            ?? (region == "cn" ? null : Servers.FirstOrDefault(s => !s.Id.Equals("bilibili", StringComparison.OrdinalIgnoreCase)))
            ?? Servers.FirstOrDefault();
        return preferred?.Options ?? new Dictionary<string, string>();
    }

    /// <summary>安装目录变更（设置卡保存后）就地生效：路径重解析 + 状态刷新，不重建列表。</summary>
    internal void UpdateInstallDir(string installDir)
    {
        if (string.Equals(_installDir, installDir, StringComparison.Ordinal))
        {
            return;
        }

        _installDir = installDir;
        OnPropertyChanged(nameof(InstallDirPath));
        _ = RefreshAsync();
    }

    /// <summary>启动游戏；忙碌或不可启动时忽略，成败写入状态提示。</summary>
    [RelayCommand]
    public async Task LaunchAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || !CanLaunch)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await launcherService.LaunchAsync(Game, _installDir, Game.Executable, cancellationToken);
            StatusText = Loc["status_launched"];
        }
        catch (Exception ex)
        {
            StatusText = Loc.Format("status_launchFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>主操作：未安装时全新安装，已安装时更新到最新版。</summary>
    [RelayCommand]
    public async Task InstallOrUpdateAsync(CancellationToken cancellationToken = default)
    {
        await RunUpdateAsync(
            () => updateService.UpdateAsync(_installDir, Game, SelectedServer, channel, Progress, cancellationToken),
            cancellationToken);
    }

    /// <summary>下载预下载包到暂存区（不覆盖现有安装），结束后刷新状态并给出结果提示。</summary>
    [RelayCommand]
    public async Task PredownloadAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        var message = "";
        try
        {
            var summary = await updateService.PredownloadAsync(
                _installDir, Game, SelectedServer, channel, Progress, cancellationToken);
            message = Loc.Format("predownload_done", summary.FromVersion, summary.ToVersion);
        }
        catch (Exception ex)
        {
            message = Loc.Format("predownload_failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(cancellationToken);
            StatusText = message;
        }
    }

    /// <summary>把已暂存的预下载包应用为正式版本。</summary>
    [RelayCommand]
    public async Task ApplyPredownloadAsync(CancellationToken cancellationToken = default)
    {
        if (!HasStagedPredownload)
        {
            return;
        }

        await RunUpdateAsync(
            () => updateService.ApplyPredownloadAsync(_installDir, Game, SelectedServer, channel, Progress, cancellationToken),
            cancellationToken);
    }

    /// <summary>更新类操作通用骨架：忙碌互斥、进度归零、异常转提示、完成后刷新状态。</summary>
    private async Task RunUpdateAsync(Func<Task<UpdateOutcome>> action, CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        ProgressText = Loc["progress_preparing"];
        var message = "";
        try
        {
            var outcome = await action();
            message = Loc.Format("progress_done", outcome.FromVersion, outcome.ToVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            message = Loc.Format("progress_failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(cancellationToken);
            StatusText = message;
        }
    }

    private IProgress<UpdateProgress>? _progress;

    /// <summary>惰性创建进度转发器（在 UI 线程上下文中捕获同步上下文）。</summary>
    private IProgress<UpdateProgress> Progress => _progress ??= new Progress<UpdateProgress>(OnProgress);

    /// <summary>下载进度回调（后台线程触发）：换算百分比并生成阶段文案。</summary>
    private void OnProgress(UpdateProgress p)
    {
        ProgressPercent = p.TotalBytes > 0
            ? Math.Clamp(p.DownloadedBytes * 100.0 / p.TotalBytes, 0, 100)
            : p.FilesTotal > 0
                ? Math.Clamp(p.FilesDone * 100.0 / p.FilesTotal, 0, 100)
                : 0;
        ProgressText = p.Phase switch
        {
            UpdatePhase.Downloading => Loc.Format("progress_downloading",
                FormatBytes(p.DownloadedBytes), FormatBytes(p.TotalBytes), p.FilesDone, p.FilesTotal),
            UpdatePhase.Patching => Loc["progress_patching"],
            UpdatePhase.Verifying => Loc["progress_verifying"],
            UpdatePhase.CleaningUp => Loc["progress_cleaning"],
            UpdatePhase.Checking => Loc["progress_checking"],
            _ => Loc["progress_finished"],
        };
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };

    /// <summary>游戏可执行文件是否存在于安装目录。</summary>
    private bool ExecutableExists() =>
        File.Exists(Path.Combine(_installDir, Game.Executable.Replace('\\', '/')));
}
