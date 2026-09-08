using Avalonia;
using Avalonia.Controls;

namespace YetAnotherGameLauncher.Controls;

/// <summary>
/// 应用自有背景层（主题渐变 + 双光晕 + 可选自定义背景图）。
/// 全窗底层与内容卡内两处使用同一结构，仅光晕的上下边距不同（<see cref="GlowMargin"/> 参数化：
/// 全窗版下移过标题色带以保证色带纯色，内容卡版随面板自身裁剪无需让位）。
/// </summary>
public partial class AppBackdrop : UserControl
{
    /// <summary>光晕层的边距（DataContext 与宿主窗口/页面板一致，绑定 AppBackgroundImage）。</summary>
    public static readonly StyledProperty<Thickness> GlowMarginProperty =
        AvaloniaProperty.Register<AppBackdrop, Thickness>(nameof(GlowMargin));

    public AppBackdrop()
    {
        InitializeComponent();
    }

    /// <summary>光晕层的边距。</summary>
    public Thickness GlowMargin
    {
        get => GetValue(GlowMarginProperty);
        set => SetValue(GlowMarginProperty, value);
    }
}
