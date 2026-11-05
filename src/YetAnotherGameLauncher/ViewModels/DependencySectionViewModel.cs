using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Dependencies;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>
/// 游戏设置页「依赖」区：列出内置依赖（CJK 字体 + 原生 RichEdit），展示五态并支持安装/修复/还原。
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
    private readonly ILogger? _logger;

    public DependencySectionViewModel(
        GameItemViewModel game,
        MainWindowViewModel owner,
        IDependencyInstaller? installer,
        string? dataHome = null,
        Func<string?>? systemWineResolver = null,
        ILogger? logger = null)
    {
        _game = game;
        _loc = owner.Loc;
        _installer = installer;
        _dataHome = dataHome;
        _systemWineResolver = systemWineResolver ?? (() => CompatTools.FindSystemWine());
        _logger = logger;

        if (installer is not null)
        {
            foreach (var manifest in installer.Dependencies)
            {
                Items.Add(new DependencyItemViewModel(this, manifest));
            }
        }

        // D3：设置页内切换启动方式/发行版时依赖区现场重算（此前重进页面才自愈）；
        // 退订在 Detach（页生命周期收尾），避免分区 VM 被共享 LaunchSettings 的委托钉住
        _game.LaunchSettings.PropertyChanged += OnLaunchSettingsPropertyChanged;

        Refresh();
    }

    /// <summary>在途安装/还原的取消令牌源（D2）；无操作在途时为 null。</summary>
    private CancellationTokenSource? _installCts;

    /// <summary>取消在途依赖操作（D2：页销毁/应用退出接线点）；无操作在途时为 no-op。</summary>
    public void CancelOngoingInstall() => _installCts?.Cancel();

    /// <summary>页生命周期收尾（D2+D3）：取消在途操作并退订 LaunchSettings。</summary>
    public void Detach()
    {
        CancelOngoingInstall();
        _game.LaunchSettings.PropertyChanged -= OnLaunchSettingsPropertyChanged;
    }

    private void OnLaunchSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LaunchSettingsViewModel.SelectedLaunchMode)
            or nameof(LaunchSettingsViewModel.SelectedProtonFlavor))
        {
            Refresh();
        }
    }

    /// <summary>依赖条目（每内置依赖一条：CJK 字体 + 原生 RichEdit）。</summary>
    public ObservableCollection<DependencyItemViewModel> Items { get; } = [];

    /// <summary>条目构造用的文案服务（条目与区共用同一实例）。</summary>
    internal ILocalizationService LocForItems => _loc;

    /// <summary>整区不可用原因原文（条目状态直接引用；null = 可用）。</summary>
    internal string? UnavailableReasonText => UnavailableReason;

    /// <summary>整区可见性：有安装器（生产装配/测试显式注入）、Linux 平台且目录非空。</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>操作互斥门：同一 prefix 一次只跑一个安装/还原。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>进度文本（下载中 42% / 解压中… / 还原中…）。</summary>
    [ObservableProperty]
    private string _progressText = "";

    /// <summary>进度条百分比（0-100；不定进度时 IsProgressIndeterminate）。</summary>
    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    /// <summary>操作结果消息槽（依赖区专属，不与位置卡的 Save 槽混用）。</summary>
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

    /// <summary>刷新全部条目状态（页构造、启动方式变化与每次操作完成后调用）。</summary>
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

    /// <summary>主按钮：未安装/待恢复 → 安装；已安装/损坏且可还原（winetricks 来源）→ 先完整还原
    /// 再安装（重装/修复）；字体来源没有还原步骤，沿用直接安装（既有语义）。</summary>
    internal Task InstallPrimaryAsync(DependencyItemViewModel item) =>
        item.CanUninstall && item.State is DependencyStateKind.Installed or DependencyStateKind.Damaged
            ? RestoreThenInstallAsync(item)
            : InstallAsync(item);

    /// <summary>安装单个依赖（条目命令转发入口）；IsBusy 互斥，失败分类映射成本地化文案。</summary>
    internal async Task InstallAsync(DependencyItemViewModel item)
    {
        if (_installer is null || IsBusy || !item.CanInstall)
        {
            return;
        }

        await RunOperationAsync(
            item,
            async (target, token) =>
            {
                var progress = new Progress<DependencyProgress>(OnProgress);
                await _installer.InstallAsync(target, item.Manifest, progress, token);
            },
            "deps_install_success");
    }

    /// <summary>还原单个依赖（卸载并完整恢复安装前状态）；已安装/损坏态可用。</summary>
    internal async Task RestoreAsync(DependencyItemViewModel item)
    {
        if (_installer is null || IsBusy || !item.CanUninstall)
        {
            return;
        }

        await RunOperationAsync(
            item,
            async (target, token) =>
            {
                SetRestoringProgress();
                await _installer.UninstallAsync(target, item.Manifest, token);
            },
            "deps_restore_success");
    }

    /// <summary>重装/修复：先完整还原（卸载），成功后再安装；卸载失败停在当前状态，不继续安装。</summary>
    private async Task RestoreThenInstallAsync(DependencyItemViewModel item)
    {
        if (_installer is null || IsBusy || !item.CanUninstall)
        {
            return;
        }

        await RunOperationAsync(
            item,
            async (target, token) =>
            {
                SetRestoringProgress();
                await _installer.UninstallAsync(target, item.Manifest, token);
                var progress = new Progress<DependencyProgress>(OnProgress);
                await _installer.InstallAsync(target, item.Manifest, progress, token);
            },
            "deps_install_success");
    }

    /// <summary>操作包装：互斥门、忙碌禁用、进度清空、取消令牌、失败分类文案与收尾刷新。</summary>
    private async Task RunOperationAsync(
        DependencyItemViewModel item,
        Func<WinePrefixTarget, CancellationToken, Task> operation,
        string successKey)
    {
        var target = ResolveTarget();
        if (target is null)
        {
            // 目标失效（启动方式等被并发改动）：整区重读，条目/原因文案一并回到可见状态
            Refresh();
            return;
        }

        IsBusy = true;
        SetItemsInstallable(false);
        Feedback.Clear();
        // 清掉上一轮的进度残留：Progress<T> 投递异步，新操作首条报告到达前进度行不能闪旧文案
        OnProgress(new DependencyProgress(DependencyPhase.Done, null));
        // D2：在途操作挂取消令牌（页销毁/应用退出经 CancelOngoingInstall 终止）
        _installCts = new CancellationTokenSource();
        try
        {
            await operation(target, _installCts.Token);
            Feedback.SetSuccess(_loc.Format(successKey, item.Title));
        }
        catch (DependencyException ex)
        {
            // 分类文案不含细节，日志必须承载排障线索（退出码/stderr/不可修计数），F49
            _logger?.LogWarning(ex, "依赖操作失败（{Kind}）：{Message}", ex.Kind, ex.Message);
            Feedback.SetFailure(_loc[ErrorKey(ex.Kind)]);
        }
        catch (OperationCanceledException)
        {
            Feedback.SetFailure(_loc["deps_install_cancelled"]);
        }
        catch (Exception ex)
        {
            // 分类学兜底：安装器升级漏网的原始异常不得静默（全局 handler 只记日志）
            _logger?.LogWarning(ex, "依赖操作意外失败：{Message}", ex.Message);
            Feedback.SetFailure(_loc.Format("deps_error_unexpected", ex.Message));
        }
        finally
        {
            _installCts.Dispose();
            _installCts = null;
            IsBusy = false;
            OnProgress(new DependencyProgress(DependencyPhase.Done, null));
            Refresh();
        }
    }

    /// <summary>还原阶段的不定进度（winetricks 卸载不提供可量化进度）。</summary>
    private void SetRestoringProgress()
    {
        IsProgressIndeterminate = true;
        ProgressPercent = 0;
        ProgressText = _loc["deps_progress_restoring"];
    }

    /// <summary>操作期间全部条目按钮置灰（Refresh 在收尾统一恢复）。</summary>
    private void SetItemsInstallable(bool installable)
    {
        foreach (var entry in Items)
        {
            entry.CanInstall = installable;
            entry.CanUninstall = installable;
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
            DependencyPhase.Installing => _loc["deps_progress_installing"],
            _ => "",
        };
    }

    /// <summary>失败分类 → 文案键（与不可用原因共用同族文案，一处一义）。</summary>
    private static string ErrorKey(DependencyFailureKind kind) => kind switch
    {
        DependencyFailureKind.WineMissing => "deps_status_unavailable_wine",
        DependencyFailureKind.PrefixMissing => "deps_status_unavailable_prefix",
        DependencyFailureKind.PrefixUnhealthy => "deps_error_unhealthy",
        DependencyFailureKind.PrefixUnsupported => "deps_error_prefix_unsupported",
        DependencyFailureKind.PrefixBusy => "deps_error_prefix_busy",
        DependencyFailureKind.WinetricksMissing => "deps_error_winetricks_missing",
        DependencyFailureKind.WinetricksFailed => "deps_error_winetricks",
        DependencyFailureKind.BackupFailed => "deps_error_backup",
        DependencyFailureKind.BackupMissing => "deps_error_backup_missing",
        DependencyFailureKind.RecoveryFailed => "deps_error_recovery",
        DependencyFailureKind.RollbackFailed => "deps_error_rollback",
        DependencyFailureKind.DllVerifyFailed => "deps_error_dllverify",
        DependencyFailureKind.OverrideFailed => "deps_error_override",
        DependencyFailureKind.RestoreFailed => "deps_error_restore",
        DependencyFailureKind.DownloadFailed => "deps_error_download",
        DependencyFailureKind.ExtractFailed => "deps_error_extract",
        DependencyFailureKind.FontCopyFailed => "deps_error_fontcopy",
        DependencyFailureKind.RegistryFailed => "deps_error_registry",
        DependencyFailureKind.StateWriteFailed => "deps_error_state",
        _ => "deps_error_unexpected",
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

/// <summary>依赖条目：展示名称/描述/五态，安装/还原按钮转发到区 VM。</summary>
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

    /// <summary>还原按钮文案。</summary>
    public string RestoreText => _loc["deps_restore"];

    /// <summary>当前状态（五态；整区不可用时归一为 NotInstalled，按钮仍禁用）。</summary>
    [ObservableProperty]
    private DependencyStateKind _state;

    /// <summary>状态文案（未安装 / 已安装 vX / 损坏 / 备份缺失 / 待恢复）。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>状态补充说明（备份缺失长文案、覆盖值校验不可用提示：单独一行换行展示，
    /// 避免状态列 CharacterEllipsis 截断）。</summary>
    [ObservableProperty]
    private string _statusDetail = "";

    /// <summary>是否显示状态补充说明。</summary>
    public bool HasStatusDetail => !string.IsNullOrEmpty(StatusDetail);

    partial void OnStatusDetailChanged(string value) => OnPropertyChanged(nameof(HasStatusDetail));

    /// <summary>是否已安装（驱动状态点颜色与重装按钮文案）。</summary>
    [ObservableProperty]
    private bool _isInstalled;

    /// <summary>当前是否可点主按钮（区可用且非忙；BackupMissing 禁用）。</summary>
    [ObservableProperty]
    private bool _canInstall;

    /// <summary>当前是否可点还原（仅已安装/损坏，区可用且非忙）。</summary>
    [ObservableProperty]
    private bool _canUninstall;

    /// <summary>是否显示还原按钮（已安装/损坏；备份缺失时整组操作隐藏避免死按钮）。</summary>
    [ObservableProperty]
    private bool _canRestore;

    /// <summary>是否显示操作按钮（BackupMissing 只显示手动恢复文案）。</summary>
    [ObservableProperty]
    private bool _showActions = true;

    /// <summary>按钮文案：安装 / 重装 / 修复 / 恢复并安装（BackupMissing 不可点）。</summary>
    public string ButtonText => State switch
    {
        DependencyStateKind.Installed => _loc["deps_reinstall"],
        DependencyStateKind.Damaged => _loc["deps_repair"],
        DependencyStateKind.NeedsRecovery => _loc["deps_recover"],
        _ => _loc["deps_install"],
    };

    partial void OnStateChanged(DependencyStateKind value) => OnPropertyChanged(nameof(ButtonText));

    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(ButtonText));

    /// <summary>按安装状态刷新展示（target 为 null = 整区不可用）。</summary>
    public void Refresh(DependencyInstallState? state)
    {
        if (state is null)
        {
            State = DependencyStateKind.NotInstalled;
            IsInstalled = false;
            StatusText = section.UnavailableReasonText ?? "";
            StatusDetail = "";
            CanInstall = false;
            CanUninstall = false;
            CanRestore = false;
            ShowActions = true;
            return;
        }

        State = state.Kind;
        IsInstalled = state.Kind == DependencyStateKind.Installed;
        StatusText = state.Kind switch
        {
            DependencyStateKind.Installed => _loc.Format("deps_status_installed", state.InstalledVersion),
            DependencyStateKind.Damaged => _loc["deps_status_damaged"],
            DependencyStateKind.BackupMissing => _loc["deps_status_backup_missing"],
            DependencyStateKind.NeedsRecovery => _loc["deps_status_needs_recovery"],
            _ => _loc["deps_status_notinstalled"],
        };
        // 覆盖值校验不可用的提示放补充行：状态列是 CharacterEllipsis，长中文会被截断
        StatusDetail = state.Kind switch
        {
            DependencyStateKind.BackupMissing => _loc.Format(
                "deps_status_backup_missing_detail", Manifest.Id),
            DependencyStateKind.Installed when state.VerificationUnavailable
                => _loc["deps_status_installed_unverified_detail"],
            _ => "",
        };
        var actionable = state.Kind != DependencyStateKind.BackupMissing;
        // 字体来源没有可还原的接管物（Core UninstallAsync 为 no-op）：不显示/不执行「还原」，
        // 避免“点了成功但文件/注册表没动”。还原只属于 winetricks 来源。
        var supportsUninstall = Manifest.WinetricksVerb is not null;
        CanInstall = actionable && !section.IsBusy;
        CanUninstall = supportsUninstall
                       && actionable
                       && state.Kind is DependencyStateKind.Installed or DependencyStateKind.Damaged
                       && !section.IsBusy;
        CanRestore = supportsUninstall
                     && state.Kind is DependencyStateKind.Installed or DependencyStateKind.Damaged;
        ShowActions = actionable;
    }

    [RelayCommand]
    private Task InstallAsync() => section.InstallPrimaryAsync(this);

    [RelayCommand]
    private Task UninstallAsync() => section.RestoreAsync(this);
}
