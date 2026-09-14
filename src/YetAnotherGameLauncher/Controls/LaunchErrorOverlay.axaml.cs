using Avalonia.Controls;

namespace YetAnotherGameLauncher.Controls;

/// <summary>启动失败覆盖层：类目化原因/技术详情/日志入口/umu 一键安装（DataContext = <see cref="YetAnotherGameLauncher.ViewModels.GameItemViewModel"/>）。</summary>
public partial class LaunchErrorOverlay : UserControl
{
    public LaunchErrorOverlay()
    {
        InitializeComponent();
    }
}
