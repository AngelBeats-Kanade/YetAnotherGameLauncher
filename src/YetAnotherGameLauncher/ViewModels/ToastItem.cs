using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>Toast 种类（决定气泡左侧指示点颜色）。</summary>
public enum ToastKind
{
    /// <summary>中性信息（强调色点）。</summary>
    Info,

    /// <summary>成功/就绪（绿色点，AppStatusOk）。</summary>
    Success,

    /// <summary>需要注意（橙色点，AppPredownloadBadge）。</summary>
    Warning,
}

/// <summary>
/// 轻提示气泡（窗口右上下角，自动消失）：承载瞬态信息的被动通知——版本检测结果、
/// 检测到游戏文件等。不排队等待：超过容量时最旧的被直接丢弃（错过即错过，状态胶囊有全量状态）。
/// </summary>
public partial class ToastItem : ViewModelBase
{
    private readonly Action<ToastItem> _dismiss;
    private readonly TimeSpan _autoDismissAfter;
    private DispatcherTimer? _autoDismissTimer;

    public ToastItem(
        string title,
        string message,
        ToastKind kind,
        TimeSpan autoDismissAfter,
        Action<ToastItem> dismiss)
    {
        Title = title;
        Message = message;
        Kind = kind;
        _autoDismissAfter = autoDismissAfter;
        _dismiss = dismiss;
    }

    /// <summary>标题（一般为游戏显示名）。</summary>
    public string Title { get; }

    /// <summary>正文（一般复用状态文案键的产物）。</summary>
    public string Message { get; }

    /// <summary>种类（决定指示点颜色）。</summary>
    public ToastKind Kind { get; }

    /// <summary>是否成功类（XAML 类切换用，避免绑定枚举）。</summary>
    public bool IsSuccess => Kind == ToastKind.Success;

    /// <summary>是否警告类（XAML 类切换用）。</summary>
    public bool IsWarning => Kind == ToastKind.Warning;

    /// <summary>
    /// 启动自动消失计时。仅 UI 线程生效——headless 测试线程直调时静默跳过，
    /// 由测试手动执行 <see cref="DismissCommand"/> 验证移除。
    /// </summary>
    internal void StartAutoDismiss()
    {
        if (_autoDismissTimer is not null || !Dispatcher.UIThread.CheckAccess())
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = _autoDismissAfter };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _dismiss(this);
        };
        _autoDismissTimer = timer;
        timer.Start();
    }

    /// <summary>用户点关闭钮立即移除（先停自灭计时器，避免迟到的空 Remove）。</summary>
    [RelayCommand]
    private void Dismiss()
    {
        _autoDismissTimer?.Stop();
        _dismiss(this);
    }
}
