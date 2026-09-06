using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
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
    ILocalizationService loc,
    GameCatalogService catalogService,
    BackgroundImageService backgroundImageService) : ViewModelBase
{
    private readonly GameUpdateService _updateService = updateService;
    private readonly GameLauncherService _launcherService = launcherService;
    private readonly IGameChannelApi _channel = channel;
    private string _installDir = installDir;
    private readonly ILocalizationService _loc = loc;
    private readonly GameCatalogService _catalogService = catalogService;

    private LaunchSettingsViewModel? _launchSettings;

    /// <summary>启动设置编辑卡（保存走 GameCatalogService 整文件原子写）。</summary>
    public LaunchSettingsViewModel LaunchSettings => _launchSettings ??= new(
        Game, _installDir, _catalogService, _loc, this);

    public GameDefinition Game { get; } = game;

    /// <summary>暴露给 XAML 的文案服务（详情页模板绑定 {Binding Loc[key]}）。</summary>
    public ILocalizationService Loc { get; } = loc;

    public string DisplayName => Game.DisplayName;

    /// <summary>列表图标：显示名首字（icon 加载失败/未配置时的回退）。</summary>
    public string IconText => string.IsNullOrEmpty(Game.DisplayName) ? "?" : Game.DisplayName[..1];

    /// <summary>官方游戏图标（games.json 的 icon 字段；异步加载，失败回退首字）。</summary>
    [ObservableProperty]
    private IImage? _gameIcon;

    [ObservableProperty]
    private bool _hasGameIcon;

    public ObservableCollection<GameServer> Servers { get; } = [.. game.Servers];

    [ObservableProperty]
    private GameServer _selectedServer = game.Servers.Count > 0 ? game.Servers[0] : new GameServer { Id = "", Name = "" };

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _canLaunch;
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private bool _predownloadAvailable;
    [ObservableProperty] private bool _hasStagedPredownload;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = "";

    /// <summary>详情页背景图（异步加载；null = 回退主题渐变）。</summary>
    [ObservableProperty]
    private IImage? _backgroundImage;

    [ObservableProperty]
    private bool _hasBackgroundImage;

    public string InstallButtonText => !IsInstalled ? _loc["game_install"] : HasUpdate ? _loc["game_update"] : _loc["game_verify"];

    /// <summary>渠道显示名（已知渠道给中文名，未知原样）。</summary>
    public string ChannelDisplayName => Game.Channel switch
    {
        "kuro" => "Kuro Games",
        "hypergryph" => "GRYPHLINE",
        var other => other,
    };

    public string InstallDirPath => _installDir;

    public string ServerCountText => _loc.Format("game_info_servers_count", Servers.Count);

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCoreAsync(cancellationToken);
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        _ = LoadBackgroundImageAsync(cancellationToken);

        var state = new LocalStateService(_installDir).Load(Game.Id, SelectedServer.Id);
        var staged = IncrementalUpdateService.TryLoadStagedManifest(_installDir);
        HasStagedPredownload = staged is not null;

        ChannelVersionInfo info;
        try
        {
            info = await _channel.GetVersionInfoAsync(SelectedServer, cancellationToken);
            StatusText = "";
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or TaskCanceledException)
        {
            StatusText = _loc["status_noConnection"];
            VersionText = state is null ? _loc["status_notInstalledShort"] : _loc.Format("status_localVersion", state.Version);
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
            ? _loc.Format("version_latest", info.LatestVersion)
            : HasUpdate
                ? _loc.Format("version_canUpdate", state.Version, info.LatestVersion)
                : _loc.Format("version_local", state.Version);

        StatusText = !IsInstalled
            ? _loc["status_notInstalled"]
            : HasUpdate ? _loc["status_hasUpdate"] : _loc["status_upToDate"];

        OnPropertyChanged(nameof(InstallButtonText));
    }

    private async Task LoadBackgroundImageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var iconTask = backgroundImageService.LoadAsync(Game.Icon, cancellationToken);
            var image = await backgroundImageService.LoadAsync(Game.BackgroundImage, cancellationToken);
            var icon = await iconTask;

            // 图像属性的赋值必须回到 UI 线程（加载续延可能在池线程上）
            Dispatcher.UIThread.Post(() =>
            {
                GameIcon = icon;
                HasGameIcon = icon is not null;
                BackgroundImage = image;
                HasBackgroundImage = image is not null;
            });
        }
        catch (Exception)
        {
            // 与 BackgroundImageService 的静默回退一致：装饰性资源失败不影响功能
        }
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
            await _launcherService.LaunchAsync(Game, _installDir, Game.Executable, cancellationToken);
            StatusText = _loc["status_launched"];
        }
        catch (Exception ex)
        {
            StatusText = _loc.Format("status_launchFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task InstallOrUpdateAsync(CancellationToken cancellationToken = default)
    {
        await RunUpdateAsync(
            () => _updateService.UpdateAsync(_installDir, Game, SelectedServer, _channel, Progress, cancellationToken),
            cancellationToken);
    }

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
            var summary = await _updateService.PredownloadAsync(
                _installDir, Game, SelectedServer, _channel, Progress, cancellationToken);
            message = _loc.Format("predownload_done", summary.FromVersion, summary.ToVersion);
        }
        catch (Exception ex)
        {
            message = _loc.Format("predownload_failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshCoreAsync(cancellationToken);
            StatusText = message;
        }
    }

    [RelayCommand]
    public async Task ApplyPredownloadAsync(CancellationToken cancellationToken = default)
    {
        if (!HasStagedPredownload)
        {
            return;
        }

        await RunUpdateAsync(
            () => _updateService.ApplyPredownloadAsync(_installDir, Game, SelectedServer, _channel, Progress, cancellationToken),
            cancellationToken);
    }

    private async Task RunUpdateAsync(Func<Task<UpdateOutcome>> action, CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        ProgressText = _loc["progress_preparing"];
        var message = "";
        try
        {
            var outcome = await action();
            message = _loc.Format("progress_done", outcome.FromVersion, outcome.ToVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            message = _loc.Format("progress_failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshCoreAsync(cancellationToken);
            StatusText = message;
        }
    }

    private IProgress<UpdateProgress>? _progress;

    /// <summary>惰性创建进度转发器（在 UI 线程上下文中捕获同步上下文）。</summary>
    private IProgress<UpdateProgress> Progress => _progress ??= new Progress<UpdateProgress>(OnProgress);

    private void OnProgress(UpdateProgress p)
    {
        ProgressPercent = p.TotalBytes > 0
            ? Math.Clamp(p.DownloadedBytes * 100.0 / p.TotalBytes, 0, 100)
            : p.FilesTotal > 0
                ? Math.Clamp(p.FilesDone * 100.0 / p.FilesTotal, 0, 100)
                : 0;
        ProgressText = p.Phase switch
        {
            UpdatePhase.Downloading => _loc.Format("progress_downloading",
                FormatBytes(p.DownloadedBytes), FormatBytes(p.TotalBytes), p.FilesDone, p.FilesTotal),
            UpdatePhase.Patching => _loc["progress_patching"],
            UpdatePhase.Verifying => _loc["progress_verifying"],
            UpdatePhase.CleaningUp => _loc["progress_cleaning"],
            UpdatePhase.Checking => _loc["progress_checking"],
            _ => _loc["progress_finished"],
        };
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };

    private bool ExecutableExists() =>
        File.Exists(Path.Combine(_installDir, Game.Executable.Replace('\\', '/')));
}
