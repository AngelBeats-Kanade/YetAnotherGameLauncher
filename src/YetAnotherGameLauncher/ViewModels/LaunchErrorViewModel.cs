using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>
/// 启动失败覆盖层：类目化的友好原因 + 可展开的技术详情 + 日志目录入口。
/// umu-launcher 未安装导致的失败附一键引导安装（安装完成后用户直接再点"启动"）。
/// </summary>
public partial class LaunchErrorViewModel : ViewModelBase
{
    private readonly ILocalizationService _loc;
    private readonly UmuLauncherInstaller? _umuInstaller;
    private readonly IPlatformInfo? _platform;
    private readonly string _umuInstallDirectory;

    public LaunchErrorViewModel(
        ILocalizationService loc,
        string message,
        string? detail = null,
        string? logPath = null,
        bool canInstallUmu = false,
        UmuLauncherInstaller? umuInstaller = null,
        IPlatformInfo? platform = null,
        string? umuInstallDirectory = null)
    {
        _loc = loc;
        _umuInstaller = umuInstaller;
        _platform = platform;
        _umuInstallDirectory = umuInstallDirectory
            ?? Path.Combine(AppPaths.DataDirectory, "umu");
        Message = message;
        Detail = detail;
        LogPath = logPath;
        CanInstallUmu = canInstallUmu && umuInstaller is not null;
        HasDetail = !string.IsNullOrEmpty(detail);
        HasLogPath = !string.IsNullOrEmpty(logPath);
    }

    /// <summary>友好失败原因（已本地化、可直接阅读）。</summary>
    [ObservableProperty]
    private string _message;

    /// <summary>技术详情（异常字符串；可展开，可复制）。</summary>
    [ObservableProperty]
    private string? _detail;

    /// <summary>是否有技术详情可展开。</summary>
    [ObservableProperty]
    private bool _hasDetail;

    /// <summary>启动日志路径（预检失败时无日志 → 隐藏入口）。</summary>
    [ObservableProperty]
    private string? _logPath;

    /// <summary>是否有日志可打开。</summary>
    [ObservableProperty]
    private bool _hasLogPath;

    /// <summary>技术详情折叠区默认收起。</summary>
    [ObservableProperty]
    private bool _detailsExpanded;

    /// <summary>是否显示"一键安装 umu-launcher"按钮（Linux 且 umu 未装且安装器可用）。</summary>
    [ObservableProperty]
    private bool _canInstallUmu;

    /// <summary>umu 安装进行中（按钮转忙碌、禁用关闭外的其它操作提示）。</summary>
    [ObservableProperty]
    private bool _isInstallingUmu;

    /// <summary>覆盖层上的补充状态（安装结果等）。</summary>
    [ObservableProperty]
    private string? _extraStatus;

    /// <summary>是否已有补充状态。</summary>
    [ObservableProperty]
    private bool _hasExtraStatus;

    /// <summary>用系统的文件管理器打开日志所在目录。</summary>
    [RelayCommand]
    private void OpenLogDirectory()
    {
        if (LogPath is not { } path)
        {
            return;
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            // 目录里启动日志按时间命名，打开后用户定位到最新一份
            _platform?.OpenDirectoryInFileManager(dir);
        }
    }

    /// <summary>收起覆盖层（下次启动失败会重建）。</summary>
    [RelayCommand]
    private void Dismiss() => DismissRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>覆盖层请求关闭（由宿主把 LaunchError 置 null）。</summary>
    public event EventHandler? DismissRequested;

    /// <summary>一键安装 umu-launcher（下载 zipapp → 解到应用数据目录）。</summary>
    [RelayCommand]
    private async Task InstallUmuAsync(CancellationToken cancellationToken)
    {
        if (_umuInstaller is null || IsInstallingUmu)
        {
            return;
        }

        IsInstallingUmu = true;
        HasExtraStatus = false;
        try
        {
            await _umuInstaller.InstallLatestAsync(_umuInstallDirectory, cancellationToken: cancellationToken)
                .ConfigureAwait(true);
            CanInstallUmu = false;
            ExtraStatus = _loc["launch_error_umu_done"];
            HasExtraStatus = true;
        }
        catch (OperationCanceledException)
        {
            // 用户取消：安静收场
        }
        catch (Exception ex)
        {
            // 安装器的 UpdateException 已是可读中文；其它异常给通用兜底
            ExtraStatus = ex.Message;
            HasExtraStatus = true;
        }
        finally
        {
            IsInstallingUmu = false;
        }
    }
}
