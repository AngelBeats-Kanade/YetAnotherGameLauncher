using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>
/// 启动失败覆盖层：类目化的友好原因 + 可展开的技术详情 + 日志目录入口。
/// 原生 umu 组件下载失败提供重试或改用本机 Proton。
/// </summary>
public partial class LaunchErrorViewModel : ViewModelBase
{
    private readonly IPlatformInfo? _platform;

    public LaunchErrorViewModel(
        string message,
        string? detail = null,
        string? logPath = null,
        IPlatformInfo? platform = null,
        bool canRetry = false,
        IReadOnlyList<string>? localProtonVersions = null)
    {
        _platform = platform;
        Message = message;
        Detail = detail;
        LogPath = logPath;
        CanRetry = canRetry;
        LocalProtonVersions = localProtonVersions ?? [];
        CanPickLocalProton = LocalProtonVersions.Count > 0;
        if (LocalProtonVersions.Count > 0)
        {
            SelectedLocalProton = LocalProtonVersions[0];
        }

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

    /// <summary>是否可重试（组件下载失败 / Runtime 缺失）。</summary>
    [ObservableProperty]
    private bool _canRetry;

    /// <summary>本机已装的 Proton 版本名（下载失败时可选手动回退）。</summary>
    public IReadOnlyList<string> LocalProtonVersions { get; }

    /// <summary>是否显示「改用本机 Proton」选择器。</summary>
    [ObservableProperty]
    private bool _canPickLocalProton;

    /// <summary>当前选中的本机 Proton 版本名。</summary>
    [ObservableProperty]
    private string? _selectedLocalProton;

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

    /// <summary>请求宿主重新执行一次启动（组件下载失败后的重试）。</summary>
    [RelayCommand]
    private void Retry() => RetryRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>覆盖层请求关闭（由宿主把 LaunchError 置 null）。</summary>
    public event EventHandler? DismissRequested;

    /// <summary>请求重新启动（宿主订阅后再次调用 LaunchAsync）。</summary>
    public event EventHandler? RetryRequested;

    /// <summary>用户选择本机 Proton 版本名后触发（宿主写入 PROTONPATH 并重试启动）。</summary>
    public event EventHandler<string>? LocalProtonSelected;

    /// <summary>确认使用下拉框中的本机 Proton。</summary>
    [RelayCommand]
    private void UseLocalProton()
    {
        if (string.IsNullOrWhiteSpace(SelectedLocalProton))
        {
            return;
        }

        LocalProtonSelected?.Invoke(this, SelectedLocalProton);
    }
}
