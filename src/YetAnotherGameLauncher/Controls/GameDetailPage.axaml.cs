using Avalonia.Controls;

namespace YetAnotherGameLauncher.Controls;

/// <summary>游戏详情页（海报式背景 + chips 簇 + 操作坞 + 启动失败覆盖层；DataContext = <see cref="YetAnotherGameLauncher.ViewModels.GameItemViewModel"/>）。</summary>
public partial class GameDetailPage : UserControl
{
    public GameDetailPage()
    {
        InitializeComponent();
    }
}
