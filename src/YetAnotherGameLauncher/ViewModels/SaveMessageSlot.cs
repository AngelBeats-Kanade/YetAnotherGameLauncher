using CommunityToolkit.Mvvm.ComponentModel;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>
/// 表单保存结果的消息槽（Message + Failed 两件套）。
/// XAML 用 save-msg 样式渲染：Failed 决定红/绿前景色，Message 空白即不显示。
/// </summary>
public sealed partial class SaveMessageSlot : ViewModelBase
{
    /// <summary>结果文本；空白 = 当前无提示。</summary>
    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _failed;

    /// <summary>是否有提示文本（驱动 XAML 显隐；空白文本不占位）。</summary>
    public bool HasMessage => Message.Length > 0;

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    /// <summary>记录一次成功结果（绿色提示）。</summary>
    public void SetSuccess(string message)
    {
        Message = message;
        Failed = false;
    }

    /// <summary>记录一次失败结果（红色提示）。</summary>
    public void SetFailure(string message)
    {
        Message = message;
        Failed = true;
    }

    /// <summary>清空提示（通常在用户重新编辑草稿或开始新的保存时调用）。</summary>
    public void Clear()
    {
        Message = "";
        Failed = false;
    }
}
