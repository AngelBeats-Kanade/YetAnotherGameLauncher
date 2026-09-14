using Avalonia.Controls;

namespace YetAnotherGameLauncher.Controls;

/// <summary>唤取记录页（鸣潮专属）：拉取/筛选 + 统计卡 + 记录列表（DataContext = <see cref="YetAnotherGameLauncher.ViewModels.GachaViewModel"/>）。</summary>
public partial class GachaPage : UserControl
{
    public GachaPage()
    {
        InitializeComponent();
    }
}
