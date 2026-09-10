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
    IFilePickerService? filePicker = null,
    Core.Abstractions.IPlatformInfo? platformInfo = null,
    UmuLauncherInstaller? umuInstaller = null) : ViewModelBase
{
    private string _installDir = installDir;

    private readonly IFilePickerService? _filePicker = filePicker;

    /// <summary>umu-launcher 引导安装器（Linux 启动失败时供错误覆盖层一键安装；null = 不可用）。</summary>
    public UmuLauncherInstaller? UmuInstaller { get; } = umuInstaller;

    /// <summary>背景视频播放器（单例共享；null = 测试场景或平台无解码能力）。</summary>
    public IVideoBackdropPlayer? VideoPlayer { get; } = videoPlayer;

    private LaunchSettingsViewModel? _launchSettings;

    /// <summary>平台环境（Linux 兼容层能力等；透传给启动设置卡）。</summary>
    public Core.Abstractions.IPlatformInfo Platform { get; } =
        platformInfo ?? (OperatingSystem.IsLinux()
            ? new Core.Services.LinuxPlatformInfo()
            : new Core.Services.WindowsPlatformInfo());

    /// <summary>启动设置编辑卡（保存走 GameCatalogService 整文件原子写）。</summary>
    public LaunchSettingsViewModel LaunchSettings => _launchSettings ??= new(
        Game, _installDir, catalogService, Loc, this, _filePicker, Platform,
        umuInstaller: UmuInstaller);

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
    /// <summary>是否满足启动条件（游戏可执行文件存在——无论是否由启动器安装登记）。</summary>
    [ObservableProperty] private bool _canLaunch;

    partial void OnCanLaunchChanged(bool value)
    {
        OnPropertyChanged(nameof(HasGachaEntry));
        OnPropertyChanged(nameof(InstallButtonText)); // 文案随"检测到游戏"状态翻转（安装 ↔ 校验修复）
        OnPropertyChanged(nameof(InstallIsPrimary));
        OnPropertyChanged(nameof(InstallIsSecondary));
    }

    /// <summary>切换服务器即刷新状态（不同服务器的安装目录/版本上下文相互独立）。</summary>
    partial void OnSelectedServerChanged(GameServer value) => _ = RefreshAsync();
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

    /// <summary>校验修复确认条可见性（包式渠道修复 = 整包重下覆盖安装，需用户确认）。</summary>
    [ObservableProperty] private bool _showRepairConfirm;

    /// <summary>校验修复确认条文案（含预计重下体积）。</summary>
    [ObservableProperty] private string _repairConfirmText = "";

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

    /// <summary>
    /// 本轮解析到的视频背景路径（与 pending 分离：离页停播不清除它，
    /// 重新进页无需重新解析即可恢复播放）。
    /// </summary>
    private string? _videoPath;

    /// <summary>详情页是否可见（切页驱动；控制视频只在页面上播放）。</summary>
    private bool _detailActive;

    /// <summary>播放器帧通知订阅状态（避免重复订阅）。</summary>
    private bool _videoSubscribed;

    /// <summary>主操作按钮文案：未安装→安装；检测到已有文件→文件式"校验修复"/包式"登记版本"；有更新→更新；已最新→校验修复。</summary>
    public string InstallButtonText => !IsInstalled
        ? CanLaunch
            ? UsesPackageManifest ? Loc["game_register"] : Loc["game_verify"]
            : Loc["game_install"]
        : HasUpdate ? Loc["game_update"] : Loc["game_verify"];

    /// <summary>主操作按钮是否为主 CTA 形态（accent）：仅"什么都没有"的全新安装；检测到游戏/已安装时退为次级。</summary>
    public bool InstallIsPrimary => !IsInstalled && !CanLaunch;

    /// <summary>主操作按钮是否为次级形态（glass）：检测到游戏或已安装时为真。</summary>
    public bool InstallIsSecondary => !InstallIsPrimary;

    /// <summary>是否库洛渠道（鸣潮专属功能如唤取记录按此显示入口）。</summary>
    public bool IsKuro => Game.Channel == "kuro";

    /// <summary>唤取记录入口可见性：仅鸣潮且游戏文件在本地（日志就在安装目录里，无需登记版本）。</summary>
    public bool HasGachaEntry => IsKuro && CanLaunch;

    /// <summary>
    /// 侧栏/状态点与状态行的预下载提示：已安装、无更新且渠道开放预下载窗口
    /// （PredownloadAvailable 已排除"已暂存待应用"的情形，避免与徽章重复提示）。
    /// </summary>
    public bool ShowPredownloadCue => IsInstalled && !HasUpdate && PredownloadAvailable;

    /// <summary>
    /// 渠道清单是否为包式（仅整包校验值，无解压后逐文件清单）。当前只有库洛是文件式；
    /// 未知渠道按包式处理——校验修复前先确认，宁可多问一次也不默默重下几十 GB。
    /// </summary>
    public bool UsesPackageManifest => !IsKuro;

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
            CanLaunch = ExecutableExists();
            OnPropertyChanged(nameof(InstallButtonText));
            OnPropertyChanged(nameof(InstallIsPrimary));
            OnPropertyChanged(nameof(InstallIsSecondary));
            OnPropertyChanged(nameof(ShowPredownloadCue));
            return;
        }

        IsInstalled = state is not null;
        CanLaunch = ExecutableExists();
        HasUpdate = VersionComparison.IsNewer(info.LatestVersion, state?.Version);
        PredownloadAvailable = info.PredownloadAvailable && !HasStagedPredownload;

        VersionText = state is null
            ? Loc.Format("version_latest", info.LatestVersion)
            : HasUpdate
                ? Loc.Format("version_canUpdate", state.Version, info.LatestVersion)
                : Loc.Format("version_local", state.Version);

        // 状态优先级：未登记但文件在 → 可直接启动（官启等来源的既有安装）；
        // 已登记 → 有更新 / 可预下载 / 已是最新
        StatusText = !IsInstalled
            ? CanLaunch ? Loc["status_detected"] : Loc["status_notInstalled"]
            : HasUpdate
                ? Loc["status_hasUpdate"]
                : PredownloadAvailable ? Loc["status_predownload"] : Loc["status_upToDate"];

        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(InstallIsPrimary));
        OnPropertyChanged(nameof(InstallIsSecondary));
        OnPropertyChanged(nameof(ShowPredownloadCue));
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
                _videoPath = videoPath;

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
                _videoPath = null;
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

    /// <summary>详情页可见性变化（MainWindowViewModel 切页驱动）：进页起播待播视频（无需重新解析），离页停止。</summary>
    internal void SetDetailActive(bool active)
    {
        _detailActive = active;
        if (!active)
        {
            StopVideo();
            return;
        }

        // 离页会清掉 pending，但已解析路径保留：同游戏切走再切回时据此恢复播放
        var path = _pendingVideoPath ?? _videoPath;
        if (path is { } videoPath)
        {
            _ = StartVideoAsync(videoPath);
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

    /// <summary>启动失败覆盖层（null = 隐藏；预检失败无日志时无日志入口）。</summary>
    [ObservableProperty]
    private LaunchErrorViewModel? _launchError;

    /// <summary>是否有待展示的启动失败覆盖层。</summary>
    public bool HasLaunchError => LaunchError is not null;

    partial void OnLaunchErrorChanged(LaunchErrorViewModel? value)
    {
        OnPropertyChanged(nameof(HasLaunchError));
        if (value is not null)
        {
            value.DismissRequested += (_, _) => LaunchError = null;
        }
    }

    /// <summary>启动游戏；忙碌时忽略，不可启动给出原因，失败弹主题化错误覆盖层。</summary>
    [RelayCommand]
    public async Task LaunchAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        if (!CanLaunch)
        {
            // 不再静默：说明为什么启动不了
            StatusText = Loc["status_launchNotReady"];
            return;
        }

        IsBusy = true;
        try
        {
            await launcherService.LaunchAsync(Game, _installDir, Game.Executable, cancellationToken);
            LaunchError = null; // 上次的失败覆盖层随成功启动清掉
            StatusText = Loc["status_launched"];
        }
        catch (LaunchException ex)
        {
            StatusText = Loc.Format("status_launchFailed", ex.Message);
            LaunchError = CreateLaunchError(ex.Message, ex.ToString(), ex.LogPath, ex.Kind);
        }
        catch (Exception ex)
        {
            StatusText = Loc.Format("status_launchFailed", ex.Message);
            LaunchError = CreateLaunchError(ex.Message, ex.ToString(), null, LaunchFailureKind.Unknown);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>按失败类目构建错误覆盖层（RuntimeMissing + umu 未装 → 提供一键安装）。</summary>
    private LaunchErrorViewModel CreateLaunchError(
        string message, string detail, string? logPath, LaunchFailureKind kind)
    {
        var umuMissing = kind == LaunchFailureKind.RuntimeMissing
            && Platform.IsLinux
            && IsUmuTemplate();
        return new LaunchErrorViewModel(
            Loc, message, detail, logPath,
            canInstallUmu: umuMissing,
            umuInstaller: UmuInstaller,
            platform: Platform);
    }

    /// <summary>当前启动模板是否走 umu（决定失败时是否提供引导安装）。</summary>
    private bool IsUmuTemplate() =>
        Game.Launch.CommandTemplate.Contains("umu-run", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 主操作：未安装时全新安装，有更新时更新；已安装且已是最新即"校验修复"——
    /// 文件式渠道直接扫描并修复缺失/损坏文件；包式渠道无逐文件清单，先弹确认再整包重下。
    /// 检测到游戏文件（未登记）的包式渠道则只登记版本，不下载任何文件。
    /// </summary>
    [RelayCommand]
    public async Task InstallOrUpdateAsync(CancellationToken cancellationToken = default)
    {
        // 检测到游戏的包式渠道：没有逐文件清单可供校验，登记版本即可（重下整包没有意义）
        if (!IsInstalled && CanLaunch && UsesPackageManifest)
        {
            await RegisterVersionAsync(cancellationToken);
            return;
        }

        if (IsInstalled && !HasUpdate && UsesPackageManifest)
        {
            // 校验修复语义下的包式渠道：拉整包清单算体积，交确认条（拉不到清单给通用文案）
            var version = new LocalStateService(_installDir).Load(Game.Id, SelectedServer.Id)?.Version ?? "";
            var totalBytes = 0L;
            try
            {
                var manifest = await channel.GetManifestAsync(SelectedServer, version, cancellationToken);
                totalBytes = manifest.Files.Sum(f => f.Size);
            }
            catch (Exception)
            {
                // 尺寸仅用于确认文案，失败不阻断
            }

            RepairConfirmText = totalBytes > 0
                ? Loc.Format("verify_confirm_msg", FormatBytes(totalBytes))
                : Loc["verify_confirm_msg_nosize"];
            ShowRepairConfirm = true;
            return;
        }

        await RunUpdateAsync(
            () => updateService.UpdateAsync(_installDir, Game, SelectedServer, channel, Progress, cancellationToken),
            isVerify: IsInstalled && !HasUpdate,
            cancellationToken);
    }

    /// <summary>
    /// 登记版本（检测到游戏文件、未登记的包式渠道）：把本地版本登记为渠道最新版，
    /// 不下载任何文件——包式渠道没有逐文件清单可校验，整包重下交由已登记态的显式确认流程。
    /// </summary>
    [RelayCommand]
    public async Task RegisterVersionAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || !CanLaunch || IsInstalled)
        {
            return;
        }

        IsBusy = true;
        var failureMessage = "";
        try
        {
            var info = await channel.GetVersionInfoAsync(SelectedServer, cancellationToken);
            await new LocalStateService(_installDir).SaveAsync(
                new LocalGameState { GameId = Game.Id, ServerId = SelectedServer.Id, Version = info.LatestVersion },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failureMessage = Loc.Format("status_registerFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(cancellationToken);
            // 成功后刷新出的"已是最新版本"即最终状态；仅失败时覆盖
            if (failureMessage.Length > 0)
            {
                StatusText = failureMessage;
            }
        }
    }

    /// <summary>确认整包重下校验修复（包式渠道）：隐藏确认条后走完整更新流程。</summary>
    [RelayCommand]
    public async Task ConfirmRepairAsync(CancellationToken cancellationToken = default)
    {
        ShowRepairConfirm = false;
        await RunUpdateAsync(
            () => updateService.UpdateAsync(_installDir, Game, SelectedServer, channel, Progress, cancellationToken),
            isVerify: true,
            cancellationToken);
    }

    /// <summary>取消校验修复确认条，不改任何文件。</summary>
    [RelayCommand]
    public void CancelRepair() => ShowRepairConfirm = false;

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
            isVerify: false,
            cancellationToken);
    }

    /// <summary>
    /// 更新类操作通用骨架：忙碌互斥、进度归零、异常转提示、完成后刷新状态。
    /// 校验语义（isVerify）完成时按修复文件数给出结果消息；其余成功操作不覆盖
    /// RefreshAsync 算出的状态行（如"可预下载新版本"），完成反馈由进度卡消失承担。
    /// </summary>
    private async Task RunUpdateAsync(
        Func<Task<UpdateOutcome>> action, bool isVerify, CancellationToken cancellationToken)
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
            if (isVerify)
            {
                message = outcome.RepairedFiles > 0
                    ? Loc.Format("verify_repaired", outcome.RepairedFiles)
                    : UsesPackageManifest ? Loc["verify_reinstalled"] : Loc["verify_ok"];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            message = Loc.Format("progress_failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(cancellationToken);
            if (!string.IsNullOrEmpty(message))
            {
                StatusText = message;
            }
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

    /// <summary>字节数格式化（数字与单位间用不换行空格，避免文案在数值与单位间断行）。</summary>
    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2}\u00A0GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1}\u00A0MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1}\u00A0KB",
        _ => $"{bytes}\u00A0B",
    };

    /// <summary>游戏可执行文件是否存在于安装目录。</summary>
    private bool ExecutableExists() =>
        File.Exists(Path.Combine(_installDir, Game.Executable.Replace('\\', '/')));
}
