using Avalonia.Controls;

namespace YetAnotherGameLauncher.Controls;

/// <summary>轻提示宿主：右上角瞬态气泡容器（数据源 = <see cref="YetAnotherGameLauncher.ViewModels.MainWindowViewModel.Toasts"/>）。</summary>
public partial class ToastHost : UserControl
{
    public ToastHost()
    {
        InitializeComponent();
    }
}
