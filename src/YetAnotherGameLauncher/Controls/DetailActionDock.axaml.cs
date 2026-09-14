using Avalonia.Controls;

namespace YetAnotherGameLauncher.Controls;

/// <summary>详情页底部操作坞：渠道/目录信息（busy 时变进度条）+ 操作按钮组（DataContext = <see cref="YetAnotherGameLauncher.ViewModels.GameItemViewModel"/>）。</summary>
public partial class DetailActionDock : UserControl
{
    public DetailActionDock()
    {
        InitializeComponent();
    }
}
