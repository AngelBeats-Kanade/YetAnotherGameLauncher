using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Services.Umu;
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
    IPlatformInfo? platformInfo = null,
    NativeUmuLauncher? nativeUmu = null,
    IUmuComponentProvisioner? umuProvisioner = null) : ViewModelBase
{
    private string _installDir = installDir;

    private readonly IFilePickerService? _filePicker = filePicker;

    /// <summary>原生 umu 启动器（Linux 内置启动链；null = 不可用/测试）。</summary>
    private readonly NativeUmuLauncher? _nativeUmu = nativeUmu;

    /// <summary>原生 umu 组件准备器（设置卡检查/下载；null = 不可用）。</summary>
    private readonly IUmuComponentProvisioner? _umuProvisioner = umuProvisioner;

    /// <summary>背景视频播放器（单例共享；null = 测试场景或平台无解码能力）。</summary>
    public IVideoBackdropPlayer? VideoPlayer { get; } = videoPlayer;

    private LaunchSettingsViewModel? _launchSettings;

    /// <summary>平台环境（Linux 兼容层能力等；透传给启动设置卡）。</summary>
    public IPlatformInfo Platform { get; } = platformInfo ?? PlatformInfoFactory.Create();

    /// <summary>启动设置编辑卡（保存走 GameCatalogService 整文件原子写）。</summary>
    public LaunchSettingsViewModel LaunchSettings => _launchSettings ??= new(
        Game, _installDir, catalogService, Loc, this, _filePicker, Platform,
        umuProvisioner: _umuProvisioner);

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

    // 版本 chip 分段（方案 A："本地 x → 最新 y"，金色数字由 XAML 按段渲染）。
    // 空 Mid/Target 表示单段展示（仅前导 + 号码）；四段均在 RefreshAsync/离线分支随状态重算。
    /// <summary>版本 chip 前导文案（"本地"/"最新版本"/离线"未安装"）。</summary>
    [ObservableProperty] private string _versionChipLead = "";
    /// <summary>版本 chip 主版本号（金色强调段之一）。</summary>
    [ObservableProperty] private string _versionChipNumber = "";
    /// <summary>版本 chip 迁移中段（"→ 最新"；空 = 无迁移可展示）。</summary>
    [ObservableProperty] private string _versionChipMid = "";
    /// <summary>版本 chip 目标版本号（有更新时的金色第二段）。</summary>
    [ObservableProperty] private string _versionChipTarget = "";
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

    /// <summary>切换服务器即刷新状态（不同服务器的安装目录/版本上下文相互独立）。
    /// 元信息行随选区即时更新：RefreshAsync 开头的变更通知在首个 await 前同步执行，无需额外补发。</summary>
    partial void OnSelectedServerChanged(GameServer value) => _ = RefreshAsync();

    /// <summary>背景视频就绪状态变化：元信息行的背景来源段随之切换。</summary>
    partial void OnHasBackgroundVideoChanged(bool value) => OnPropertyChanged(nameof(DetailMetaText));

    /// <summary>背景图加载状态变化：元信息行的背景来源段随之切换。</summary>
    partial void OnHasBackgroundImageChanged(bool value) => OnPropertyChanged(nameof(DetailMetaText));
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

    /// <summary>版本检测结果按服务器的会话缓存（进行中或已成功的任务；每启动每服务器至多一次网络检测）。</summary>
    private readonly Dictionary<GameServer, Task<ChannelVersionInfo>> _versionInfoTasks = [];

    /// <summary>当前展示资产对应的区域（null = 尚未加载过；语言切换换区时触发重新解析）。</summary>
    private string? _loadedRegion;

    /// <summary>资产缓存对齐的游戏版本（磁盘缓存元数据记录值；版本检测成功后随之更新）。</summary>
    private string? _assetVersion;

    /// <summary>_assetVersion 是否已从磁盘缓存元数据装载（每会话一次读盘）。</summary>
    private bool _assetVersionLoaded;

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

    /// <summary>
    /// 详情页标题下的元信息行（方案 A）：渠道 · 服务器 · 背景来源。
    /// 服务器段取当前服务器名（空名回退数量文案）；无背景素材时来源段整体省略。
    /// </summary>
    public string DetailMetaText
    {
        get
        {
            var segments = new List<string> { ChannelDisplayName, ServerMetaSegment };
            var source = BackgroundSourceText;
            if (source.Length > 0)
            {
                segments.Add(source);
            }

            return string.Join(" · ", segments);
        }
    }

    /// <summary>元信息行的服务器段：优先服务器名，缺失回退"数量"文案。</summary>
    private string ServerMetaSegment
    {
        get
        {
            var name = SelectedServer.Name;
            return name.Length > 0 ? name : ServerCountText;
        }
    }

    /// <summary>背景来源段：视频循环中 &gt; 静态图 &gt; 无（空串 = 省略段）。</summary>
    private string BackgroundSourceText => HasBackgroundVideo
        ? Loc["detail_meta_video"]
        : HasBackgroundImage ? Loc["detail_meta_image"] : "";

    /// <summary>
    /// 刷新安装状态/版本/预下载可用性（语言或渠道数据变化后也会调用）。
    /// 版本/预载检测每服务器每启动至多一次（会话缓存），后续刷新零网络；
    /// 资产（图标/背景）只在区域变化或检测到的游戏版本变化时重新解析，其余情况保持启动预加载结果。
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // 语言可能已切换：显示名/图标首字随语言重建（元信息行的本地化段同批刷新）
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IconText));
        OnPropertyChanged(nameof(DetailMetaText));

        var state = new LocalStateService(_installDir).Load(Game.Id, SelectedServer.Id);
        var staged = IncrementalUpdateService.TryLoadStagedManifest(_installDir);
        HasStagedPredownload = staged is not null;

        ChannelVersionInfo info;
        try
        {
            info = await GetVersionInfoCachedAsync(SelectedServer, cancellationToken);
            StatusText = "";
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or TaskCanceledException)
        {
            StatusText = Loc["status_noConnection"];
            SetVersionChip(state?.Version, latestVersion: null, hasUpdate: false);
            IsInstalled = state is not null;
            HasUpdate = false;
            PredownloadAvailable = false;
            CanLaunch = ExecutableExists();
            OnPropertyChanged(nameof(InstallButtonText));
            OnPropertyChanged(nameof(InstallIsPrimary));
            OnPropertyChanged(nameof(InstallIsSecondary));
            OnPropertyChanged(nameof(ShowPredownloadCue));

            // 离线启动也要有缓存资产兜底：本会话尚未加载过资产时补一次纯磁盘加载（零网络）
            var offlineRegion = RegionForLanguage(Loc.EffectiveCulture);
            if (!string.Equals(_loadedRegion, offlineRegion, StringComparison.Ordinal))
            {
                _ = LoadAssetsCoreAsync(remote: false, reloadIcon: false, cancellationToken);
            }

            return;
        }

        // 版本检测成功：区域变化（语言切换）或版本变化（含首次检测）才重走资产解析链路——
        // 版本门控下缓存一致即零网络命中；版本变化时 http 图标一并强制重取
        var region = RegionForLanguage(Loc.EffectiveCulture);
        EnsureAssetVersionLoaded();
        var versionChanged = !string.Equals(_assetVersion, info.LatestVersion, StringComparison.Ordinal);
        var regionChanged = !string.Equals(_loadedRegion, region, StringComparison.Ordinal);
        if (versionChanged || regionChanged)
        {
            _assetVersion = info.LatestVersion;
            _ = LoadAssetsCoreAsync(remote: true, reloadIcon: versionChanged && IsHttpIcon, cancellationToken);
        }

        IsInstalled = state is not null;
        CanLaunch = ExecutableExists();
        HasUpdate = VersionComparison.IsNewer(info.LatestVersion, state?.Version);
        PredownloadAvailable = info.PredownloadAvailable && !HasStagedPredownload;

        SetVersionChip(state?.Version, info.LatestVersion, HasUpdate);

        // 状态优先级：未登记但文件在 → 可直接启动（官启等来源的既有安装）；
        // 已登记 → 有更新 / 可预下载 / 已是最新
        var newStatus = !IsInstalled
            ? CanLaunch ? Loc["status_detected"] : Loc["status_notInstalled"]
            : HasUpdate
                ? Loc["status_hasUpdate"]
                : PredownloadAvailable ? Loc["status_predownload"] : Loc["status_upToDate"];
        RaiseStatusToast(newStatus);
        StatusText = newStatus;

        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(InstallIsPrimary));
        OnPropertyChanged(nameof(InstallIsSecondary));
        OnPropertyChanged(nameof(ShowPredownloadCue));
    }

    /// <summary>
    /// 组装版本 chip 分段文案：未登记→"最新版本 x"（离线且无远端信息→"未安装"）；
    /// 有更新→"本地 x → 最新 y"；其余→"本地 x"。金色数字由 XAML 按段渲染，此处只管分段。
    /// 更新判定沿用调用方刚算出的 <paramref name="hasUpdate"/>（单一事实源，避免 chip 与
    /// 状态点/状态文案各算各的）；离线已安装沿用"本地 x"措辞，不再保留旧版的"本地版本 x"，
    /// 使 chip 在线/离线前后一致（2026-09-16 决策）。
    /// </summary>
    private void SetVersionChip(string? localVersion, string? latestVersion, bool hasUpdate)
    {
        if (localVersion is null)
        {
            VersionChipLead = latestVersion is not null ? Loc["version_label_latest"] : Loc["status_notInstalledShort"];
            VersionChipNumber = latestVersion ?? "";
            VersionChipMid = "";
            VersionChipTarget = "";
            return;
        }

        VersionChipLead = Loc["version_label_local"];
        VersionChipNumber = localVersion;
        VersionChipMid = hasUpdate ? Loc["version_mid_update"] : "";
        VersionChipTarget = hasUpdate ? latestVersion! : "";
    }

    /// <summary>启动预加载：图标与背景仅读磁盘缓存（零网络），启动时对全部游戏并行调用；
    /// 缓存未命中（首次运行/换了区域）保持占位，待版本检测成功后的刷新链路补拉。</summary>
    public Task PreloadAssetsAsync(CancellationToken cancellationToken = default) =>
        LoadAssetsCoreAsync(remote: false, reloadIcon: false, cancellationToken);

    /// <summary>清空版本检测会话缓存（internal 供单测模拟"重启后重新检测"；生产语义为每启动检测一次，运行期不重置）。</summary>
    internal void ResetVersionCheckCache() => _versionInfoTasks.Clear();

    /// <summary>版本检测（每服务器每启动至多一次网络）：同服务器复用会话缓存，进行中任务并发去重；失败不缓存（下次刷新重试）。</summary>
    private async Task<ChannelVersionInfo> GetVersionInfoCachedAsync(
        GameServer server, CancellationToken cancellationToken)
    {
        if (_versionInfoTasks.TryGetValue(server, out var cached))
        {
            return await cached;
        }

        var task = channel.GetVersionInfoAsync(server, cancellationToken);
        _versionInfoTasks[server] = task;
        try
        {
            return await task;
        }
        catch
        {
            // 检测失败不作缓存：同服务器后续刷新重试（离线会话保持旧行为）
            if (ReferenceEquals(_versionInfoTasks.GetValueOrDefault(server), task))
            {
                _versionInfoTasks.Remove(server);
            }

            throw;
        }
    }

    /// <summary>确保 _assetVersion 已从背景缓存元数据装载（免网络对齐磁盘缓存记录的版本）。</summary>
    private void EnsureAssetVersionLoaded()
    {
        if (_assetVersionLoaded)
        {
            return;
        }

        _assetVersionLoaded = true;
        _assetVersion = backdropService.GetCachedGameVersion(Game.Id);
    }

    /// <summary>图标是否为 http 直链（版本变化时需要绕过缓存强制重取的类型；avares/本地文件本就随包/在盘）。</summary>
    private bool IsHttpIcon =>
        Uri.TryCreate(Game.Icon, UriKind.Absolute, out var iconUri) && iconUri.Scheme is "http" or "https";

    /// <summary>
    /// 加载图标与背景资产：remote=false 仅读磁盘缓存（启动预加载，零网络）；
    /// remote=true 走版本门控的远程解析（版本/区域一致时同样命中磁盘缓存零网络）。
    /// reloadIcon=true 时图标绕过缓存重取（仅 http 图标有实际网络动作）。装饰性资源失败静默回退。
    /// </summary>
    private async Task LoadAssetsCoreAsync(bool remote, bool reloadIcon, CancellationToken cancellationToken)
    {
        var region = RegionForLanguage(Loc.EffectiveCulture);
        _loadedRegion = region; // 进入即记录：预加载与版本刷新并发触发时不重复解析
        EnsureAssetVersionLoaded();

        // 图标与背景分别兜底：背景链路失败不应吞掉图标（图标失败同样回退首字贴片）
        try
        {
            var icon = reloadIcon
                ? await backgroundImageService.ReloadAsync(Game.Icon, cancellationToken)
                : await backgroundImageService.LoadAsync(Game.Icon, cancellationToken);
            GameIcon = icon;
            HasGameIcon = icon is not null;
        }
        catch (Exception)
        {
            // 装饰性资源失败不影响功能
        }

        try
        {
            // 背景来源（配置文件不携带背景地址；版本门控决定是否向渠道确认当期地址）：
            // 渠道背景服务（含磁盘缓存）→ null 时回退主题渐变。
            // 视频背景：先上海报（官方首帧图/静态兜底），首帧解码到达后视频层再接管
            var request = new BackdropRequest(Game.Id, Game.Channel, region, _installDir, SelectServerOptions(region));
            var backdrop = remote
                ? await backdropService.ResolveAsync(request, _assetVersion, cancellationToken)
                : await backdropService.ResolveCachedAsync(request);

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
                // 仅活动页才触碰共享播放器：其他游戏的后台资产加载不得停掉正在播放的背景视频
                if (_detailActive)
                {
                    StopVideo();
                }
                else
                {
                    _pendingVideoPath = null;
                }

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

    /// <summary>播放器帧就绪：首帧到达后隐藏海报、显示视频层（幂等，重设同值不触发通知）。
    /// 以帧缓冲非空为准——停止瞬间迟到的陈旧通知（共享播放器上一游戏最后一帧经 UI 线程
    /// 异步投递）不得点亮新页面的视频层。</summary>
    private void OnVideoFrameUpdated(object? sender, EventArgs e) =>
        HasBackgroundVideo = VideoPlayer?.Frame is not null;

    /// <summary>瞬态状态变化通知（主窗口转发为右下角轻提示）：检测到游戏文件/有更新/可预下载。</summary>
    public event Action<string, string, ToastKind>? StatusToastRequested;

    /// <summary>状态文案的语义键（跨语言稳定）：RefreshAsync 早期会把 StatusText 清空，
    /// toast 的"是否变化"判定必须用这里记录的上次语义，而非 StatusText 现值——否则每次刷新
    /// （含语言切换）都会误判为变化而重复弹泡。</summary>
    private string? _lastStatusSemantic;

    /// <summary>首轮刷新（启动预热）不弹——状态胶囊本就承载；此后状态语义变化且属于值得被动
    /// 告知的类别（检测到游戏/有更新/可预下载）才弹，已是最新/未安装/断网静默。</summary>
    private void RaiseStatusToast(string newStatus)
    {
        var semantic = newStatus == Loc["status_detected"] ? "detected"
            : newStatus == Loc["status_hasUpdate"] ? "hasUpdate"
            : newStatus == Loc["status_predownload"] ? "predownload"
            : "other";
        var previous = _lastStatusSemantic;
        _lastStatusSemantic = semantic;
        var wasArmed = _statusToastArmed;
        _statusToastArmed = true;
        if (!wasArmed || previous == semantic || semantic == "other")
        {
            return;
        }

        StatusToastRequested?.Invoke(
            DisplayName, newStatus, semantic == "detected" ? ToastKind.Success : ToastKind.Warning);
    }

    /// <summary>状态轻提示已武装（首轮刷新完成后置位；见 <see cref="RaiseStatusToast"/>）。</summary>
    private bool _statusToastArmed;

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
            // 不带 Platform.IsLinux 门：Windows 上也走原生链入口，由 NativeUmuLauncher.EnsureLinux
            // 抛出明确的「仅支持 Linux」错误，而不是把 native-umu 当命令名报「找不到」
            if (IsNativeUmuTemplate() && _nativeUmu is not null)
            {
                var proton = CompatTools.ResolveNativeProtonRequest(Game.Launch.Environment);
                var progress = new Progress<string>(msg => StatusText = msg);
                await _nativeUmu.LaunchAsync(
                    Game.Id, _installDir, Game.Executable, proton,
                    extraEnvironment: Game.Launch.Environment,
                    progress: progress,
                    cancellationToken: cancellationToken,
                    umuId: Game.Launch.UmuId);
            }
            else
            {
                await launcherService.LaunchAsync(Game, _installDir, Game.Executable, cancellationToken);
            }

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

    /// <summary>按失败类目构建错误覆盖层：原生组件失败→重试/选本机 Proton。</summary>
    private LaunchErrorViewModel CreateLaunchError(
        string message, string detail, string? logPath, LaunchFailureKind kind)
    {
        var canRetry = Platform.IsLinux && kind is
            LaunchFailureKind.ProtonDownloadFailed
            or LaunchFailureKind.UmuRuntimeDownloadFailed
            or LaunchFailureKind.UmuRuntimeMissing;
        var localProtons = Platform.IsLinux && kind == LaunchFailureKind.ProtonDownloadFailed
            ? CompatTools.FindProtonVersions()
            : [];
        var error = new LaunchErrorViewModel(
            message, detail, logPath,
            platform: Platform,
            failureKind: kind,
            canRetry: canRetry,
            localProtonVersions: localProtons);
        error.RetryRequested += OnLaunchErrorRetryRequested;
        error.LocalProtonSelected += OnLaunchErrorLocalProtonSelected;
        return error;
    }

    /// <summary>错误覆盖层「重试」：清掉覆盖层后重新启动一次。</summary>
    private async void OnLaunchErrorRetryRequested(object? sender, EventArgs e)
    {
        LaunchError = null;
        await LaunchAsync();
    }

    /// <summary>改用本机 Proton：写入 PROTONPATH 环境并落盘，然后重新启动。</summary>
    private async void OnLaunchErrorLocalProtonSelected(object? sender, string protonVersion)
    {
        var path = CompatTools.LocateProton(protonVersion);
        if (path is null)
        {
            return;
        }

        Game.Launch.Environment["PROTONPATH"] = path;
        try
        {
            await catalogService.SaveAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UpdateException)
        {
            // 保存失败不阻断本次启动：内存中已生效
        }

        LaunchError = null;
        await LaunchAsync();
    }

    /// <summary>当前启动模板是否为原生 umu（内置 C# 启动链）。</summary>
    private bool IsNativeUmuTemplate() =>
        Game.Launch.CommandTemplate.Contains("native-umu", StringComparison.OrdinalIgnoreCase);

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
