using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Core.Dependencies;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.ViewModels;/// <summary>
/// 游戏设置页「依赖」区：列出内置依赖（首版仅 CJK 字体），展示安装状态并支持手动安装。
/// 目标（wine + prefix）按当前启动方式现场解析；不可用是可见状态而非报错——
/// Direct 模式 / 缺 wine / 缺 Proton / prefix 未初始化各自给可操作文案。
/// 自带独立消息槽与进度行，不与设置页的保存消息槽混用（UI_STRUCTURE §4 纪律）。
/// </summary>
public sealed partial class DependencySectionViewModel : ViewModelBase
{
    private readonly GameItemViewModel _game;
    private readonly ILocalizationService _loc;
    private readonly IDependencyInstaller? _installer;
    private readonly string? _dataHome;
    private readonly Func<string?> _systemWineResolver;

    public DependencySectionViewModel(
        GameItemViewModel game,
        MainWindowViewModel owner,
        IDependencyInstaller? installer,
        string? dataHome = null,
        Func<string?>? systemWineResolver = null)
    {
        _game = game;
        _loc = owner.Loc;
        _installer = installer;
        _dataHome = dataHome;
        _systemWineResolver = systemWineResolver ?? (() => CompatTools.FindSystemWine());

        if (installer is not null)
        {
            foreach (var manifest in installer.Dependencies)
            {
                Items.Add(new DependencyItemViewModel(this, manifest));
            }
        }

        Refresh();
    }

    /// <summary>依赖条目（每内置依赖一条）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<DependencyItemViewModel> Items { get; } = [];

    /// <summary>条目构造用的文案服务（条目与区共用同一实例）。</summary>
    internal ILocalizationService LocForItems => _loc;

    /// <summary>整区不可用原因原文（条目状态直接引用；null = 可用）。</summary>
    internal string? UnavailableReasonText => UnavailableReason;

    /// <summary>整区可见性：有安装器（生产装配/测试显式注入）、Linux 平台且目录非空。</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>安装互斥门：同一 prefix 一次只跑一个安装。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>安装进度文本（下载中 42% / 解压中…）。</summary>
    [ObservableProperty]
    private string _progressText = "";

    /// <summary>进度条百分比（0-100；不定进度时 IsProgressIndeterminate）。</summary>
    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    /// <summary>安装结果消息槽（依赖区专属，不与位置卡的 Save 槽混用）。</summary>
    public SaveMessageSlot Feedback { get; } = new();

    /// <summary>整区不可用原因文案（null = 可用，逐项可装）。</summary>
    [ObservableProperty]
    private string? _unavailableReason;

    /// <summary>是否有整区不可用原因（驱动原因行显隐）。</summary>
    public bool HasUnavailableReason => UnavailableReason is not null;

    partial void OnUnavailableReasonChanged(string? value) => OnPropertyChanged(nameof(HasUnavailableReason));

    /// <summary>按当前启动方式解析安装目标；不可用返回 null（原因写入 UnavailableReason）。</summary>
    private WinePrefixTarget? ResolveTarget()
    {
        var launch = _game.LaunchSettings;
        var mode = launch.SelectedLaunchMode?.Mode ?? LaunchMode.Direct;
        var protonRequest = mode == LaunchMode.NativeUmu ? launch.SelectedProtonFlavor : null;
        var resolution = WinePrefixTargetResolver.Resolve(
            mode,
            _game.Game.Id,
            _game.Game.Launch.Environment,
            protonRequest,
            _systemWineResolver(),
            _dataHome);
        UnavailableReason = resolution.Reason is { } reason
            ? _loc[ReasonKey(reason)]
            : null;
        return resolution.Target;
    }

    /// <summary>刷新全部条目状态（页构造与每次安装完成后调用）。</summary>
    public void Refresh()
    {
        if (_installer is null || !_game.LaunchSettings.IsLinux || Items.Count == 0)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        var target = ResolveTarget();
        foreach (var item in Items)
        {
            item.Refresh(target is null ? null : _installer!.GetState(target, item.Manifest));
        }
    }

    /// <summary>安装单个依赖（条目命令转发入口）；IsBusy 互斥，失败分类映射成本地化文案。</summary>
    internal async Task InstallAsync(DependencyItemViewModel item)
    {
        if (_installer is null || IsBusy || !item.CanInstall)
        {
            return;
        }

        var target = ResolveTarget();
        if (target is null)
        {
            item.Refresh(null);
            return;
        }

        IsBusy = true;
        Feedback.Clear();
        try
        {
            var progress = new Progress<DependencyProgress>(OnProgress);
            await _installer.InstallAsync(target, item.Manifest, progress).ConfigureAwait(true);
            Feedback.SetSuccess(_loc.Format("deps_install_success", item.Title));
        }
        catch (DependencyException ex)
        {
            Feedback.SetFailure(_loc[ErrorKey(ex.Kind)]);
        }
        catch (OperationCanceledException)
        {
            Feedback.SetFailure(_loc["deps_install_cancelled"]);
        }
        finally
        {
            IsBusy = false;
            OnProgress(new DependencyProgress(DependencyPhase.Done, null));
            Refresh();
        }
    }

    /// <summary>进度报告 → 进度行（Progress&lt;T&gt; 回调线程不定，仅触 UI 属性）。</summary>
    private void OnProgress(DependencyProgress progress)
    {
        IsProgressIndeterminate = progress.Phase != DependencyPhase.Done && progress.Fraction is null;
        if (progress.Fraction is { } fraction)
        {
            ProgressPercent = fraction * 100;
        }

        ProgressText = progress.Phase switch
        {
            DependencyPhase.Downloading when progress.Fraction is { } f
                => _loc.Format("deps_progress_downloading", (int)Math.Round(f * 100)),
            DependencyPhase.Downloading => _loc["deps_progress_downloading_plain"],
            DependencyPhase.Extracting => _loc["deps_progress_extracting"],
            DependencyPhase.Copying => _loc["deps_progress_copying"],
            DependencyPhase.Registering => _loc["deps_progress_registering"],
            _ => "",
        };
    }

    /// <summary>失败分类 → 文案键（与不可用原因共用同族文案，一处一义）。</summary>
    private static string ErrorKey(DependencyFailureKind kind) => kind switch
    {
        DependencyFailureKind.WineMissing => "deps_status_unavailable_wine",
        DependencyFailureKind.PrefixMissing => "deps_status_unavailable_prefix",
        DependencyFailureKind.DownloadFailed => "deps_error_download",
        DependencyFailureKind.ExtractFailed => "deps_error_extract",
        DependencyFailureKind.FontCopyFailed => "deps_error_fontcopy",
        DependencyFailureKind.RegistryFailed => "deps_error_registry",
        _ => "deps_error_registry",
    };

    private static string ReasonKey(WineTargetUnavailable reason) => reason switch
    {
        WineTargetUnavailable.DirectMode => "deps_status_unavailable_direct",
        WineTargetUnavailable.WineMissing => "deps_status_unavailable_wine",
        WineTargetUnavailable.ProtonMissing => "deps_status_unavailable_proton",
        WineTargetUnavailable.PrefixMissing => "deps_status_unavailable_prefix",
        _ => "deps_status_unavailable_proton",
    };
}

/// <summary>依赖条目：展示名称/描述/状态，安装按钮转发到区 VM。</summary>
public sealed partial class DependencyItemViewModel(DependencySectionViewModel section, DependencyManifest manifest)
    : ViewModelBase
{
    private readonly ILocalizationService _loc = section.LocForItems;

    /// <summary>依赖清单。</summary>
    public DependencyManifest Manifest { get; } = manifest;

    /// <summary>显示名（deps_{id}_name）。</summary>
    public string Title => _loc[$"deps_{Manifest.Id}_name"];

    /// <summary>说明（deps_{id}_desc）。</summary>
    public string Description => _loc[$"deps_{Manifest.Id}_desc"];

    /// <summary>状态文案（未安装 / 已安装 vX / 整区不可用原因）。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>是否已安装（驱动状态点颜色与按钮文案）。</summary>
    [ObservableProperty]
    private bool _isInstalled;

    /// <summary>当前是否可点安装（区可用且非忙）。</summary>
    [ObservableProperty]
    private bool _canInstall;

    /// <summary>按钮文案：未安装=安装，已安装=重装。</summary>
    public string ButtonText => IsInstalled ? _loc["deps_reinstall"] : _loc["deps_install"];

    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(ButtonText));

    /// <summary>按安装状态刷新展示（target 为 null = 整区不可用）。</summary>
    public void Refresh(DependencyInstallState? state)
    {
        if (state is null)
        {
            IsInstalled = false;
            StatusText = section.UnavailableReasonText ?? "";
            CanInstall = false;
            return;
        }

        IsInstalled = state.Installed;
        StatusText = state.Installed
            ? _loc.Format("deps_status_installed", state.InstalledVersion)
            : _loc["deps_status_notinstalled"];
        CanInstall = !section.IsBusy;
    }

    [RelayCommand]
    private Task InstallAsync() => section.InstallAsync(this);
}
