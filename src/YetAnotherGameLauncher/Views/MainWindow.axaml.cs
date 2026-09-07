using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.Views;

/// <summary>
/// 主窗口代码后置：侧栏选中指示点动画。
/// 指示点是覆盖在侧栏上的共享 Border，展开态位于选中项内部左缘（收起态贴侧栏左缘——
/// 收起行内被图标占满，内置会压图标）；RenderTransform 为 TransformGroup
/// （先 Scale 后 Translate、原点 0,0）→ 视觉区间 = [TranslateY, TranslateY+Height×ScaleY]。
/// 选中项变更时写入最终基值并播放两段式编舞：①旧项上朝行进方向变长一倍 → 快速跳到新项 →
/// ②以逆向拉长形态落位后收缩回小点（两段各自贴着新旧项，不把两行连成一条；
/// 动画进行中动画值覆盖基值，结束后回落到基值即终态；headless 会话不执行动画，
/// 基值始终可见，测试因此可直接断言渲染位置）。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>静态"小点"的视觉高度（px）。须小于最小的导航行高，保证静息时完整落在选中项内。</summary>
    internal const double DotHeight = 16;

    /// <summary>展开态下指示点相对选中项左缘的内缩距离（px）：完全进入项内、不压内容。</summary>
    private const double InsideItemInsetX = 3;

    /// <summary>收起态下指示点贴侧栏左缘的位置（px）。</summary>
    private const double CollapsedEdgeX = 3;

    /// <summary>迁移动画总时长：0-45% 旧项上变长、45-55% 跳变、55-100% 新项上收缩。</summary>
    private static readonly TimeSpan TransferDuration = TimeSpan.FromMilliseconds(420);

    /// <summary>平移段的缓入缓出贝塞尔样条（标准 ease-in-out 控制点）。</summary>
    private static readonly KeySpline EaseInOutSpline = new(0.42, 0, 0.58, 1);

    /// <summary>拉长/缩短段的缓出贝塞尔样条（标准 ease-out 控制点）。</summary>
    private static readonly KeySpline EaseOutSpline = new(0, 0, 0.58, 1);

    /// <summary>在途迁移动画的取消源：快速连点时中断上一段，从当前视觉状态重新出发。</summary>
    private CancellationTokenSource? _indicatorCts;

    /// <summary>合并同一 UI 批次里的多次触发（选中变化与高亮归属变化往往同时发生）。</summary>
    private bool _indicatorMoveQueued;

    /// <summary>空闲校验是否已排队（保证同批次只排一次）。</summary>
    private bool _indicatorVerifyQueued;

    /// <summary>是否已有落位（决定变更时播迁移动画还是直接落位）。</summary>
    private bool _indicatorPlaced;

    /// <summary>上一次指示点指向的目标元素：变化才播迁移动画，布局校正一律直接吸附。</summary>
    private Control? _lastTarget;

    /// <summary>
    /// 测试开关：headless 会话不推进动画时钟，迁移动画会冻结在首帧并遮盖基值终态；
    /// 关闭后指示点直接落位（落位/几何逻辑不变），动画本身由真机验证覆盖。
    /// </summary>
    internal bool NavIndicatorAnimationEnabled { get; set; } = true;

    /// <summary>已挂钩属性通知的 VM（DataContext 替换时先摘除旧订阅）。</summary>
    private MainWindowViewModel? _hookedVm;

    /// <summary>列表内部 ScrollViewer（模板应用后捕获一次，滚动时跟随重算落位）。</summary>
    private ScrollViewer? _gamesScroll;

    // XAML 编译器只给控件生成 x:Name 字段，Transform 需按 AXAML 中的声明顺序解析（Scale 在前、Translate 在后）
    private ScaleTransform IndicatorScale =>
        (ScaleTransform)((TransformGroup)NavIndicator.RenderTransform!).Children[0];

    private TranslateTransform IndicatorTranslate =>
        (TranslateTransform)((TransformGroup)NavIndicator.RenderTransform!).Children[1];

    /// <summary>指示点当前基态顶边 t（视觉区间 [t, t+Height×ScaleY]，供无头测试断言渲染位置）。</summary>
    internal double IndicatorTop { get; private set; }

    /// <summary>指示点当前基态左缘 X（供无头测试断言静息时完整落在选中项内）。</summary>
    internal double IndicatorLeft { get; private set; }

    /// <summary>指示点当前基态纵向缩放（静态小点 = DotHeight/Height）。</summary>
    internal double IndicatorScaleY { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        GamesList.SelectionChanged += (_, _) => QueueIndicatorMove();
        GamesList.TemplateApplied += OnGamesListTemplateApplied;
        // 每次布局后校正落位：首次上屏时 Opened 触发点早于最终布局，几何需要收敛
        GamesList.LayoutUpdated += (_, _) => QueueIndicatorMove();
        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => QueueIndicatorMove(); // 首次上屏后按当前选中项直接落位
        PropertyChanged += OnWindowPropertyChanged; // 窗口状态 → 圆角/卡片边距与标题栏图标
        SizeChanged += OnWindowSizeChanged; // 窗口宽度 → 侧栏阈值自适应收放
    }

    /// <summary>窗口状态变化：最大化时去圆角与卡片边距，并切换最大化/还原图标。</summary>
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        ContentCard.Classes.Set("maximized", maximized);
        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
    }

    /// <summary>窗口尺寸变化：转发 VM 做侧栏阈值自适应（穿越阈值自动收放，宽度过渡自带动画）。</summary>
    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e) =>
        (DataContext as MainWindowViewModel)?.SetWindowWidth(e.NewSize.Width);

    /// <summary>自绘标题栏拖拽：按钮命中不拖拽；最大化时忽略。</summary>
    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsWithinCaptionButton(e.Source as Visual) || WindowState == WindowState.Maximized)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>自绘标题栏双击：切换最大化/还原（按钮命中除外）。</summary>
    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsWithinCaptionButton(e.Source as Visual))
        {
            return;
        }

        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>按下命中的元素是否在某个按钮内（按钮自身处理点击，不触发标题栏拖拽）。</summary>
    private static bool IsWithinCaptionButton(Visual? source)
    {
        for (var v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is Button)
            {
                return true;
            }
        }

        return false;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>监听 VM 属性变化：高亮归属（游戏↔设置↔关于）与侧栏收放（行高变化）都影响落位。</summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_hookedVm is not null)
        {
            _hookedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _hookedVm = null;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            _hookedVm = vm;
            QueueIndicatorMove();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsGameNavActive)
                or nameof(MainWindowViewModel.IsSettingsNavActive)
                or nameof(MainWindowViewModel.IsAboutNavActive)
                or nameof(MainWindowViewModel.IsSidebarExpanded))
        {
            QueueIndicatorMove();
        }
    }

    /// <summary>模板应用后捕获列表内部 ScrollViewer，滚动时重算落位（多游戏可滚动场景）。</summary>
    private void OnGamesListTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (_gamesScroll is not null)
        {
            return;
        }

        _gamesScroll = GamesList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_gamesScroll is not null)
        {
            _gamesScroll.ScrollChanged += (_, _) => QueueIndicatorMove();
        }
    }

    /// <summary>合并触发源：排队到布局之后执行（Background 优先级低于 Layout），读到最新几何。</summary>
    private void QueueIndicatorMove()
    {
        if (_indicatorMoveQueued)
        {
            return;
        }

        _indicatorMoveQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _indicatorMoveQueued = false;
                MoveNavIndicator();
                QueueIndicatorVerify(); // 落位后空闲复核，兜住布局序列中读到的过渡几何
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// 解析当前选中项对应的侧栏元素并把指示点移动过去（无目标时隐藏）。
    /// 返回是否有可见值发生变化，供空闲校验判断几何是否仍在收敛。
    /// </summary>
    private bool MoveNavIndicator()
    {
        var indicator = NavIndicator;
        if (indicator.Parent is not Panel overlay)
        {
            return false;
        }

        var target = ResolveNavTarget();
        if (target is null || !target.IsAttachedToVisualTree())
        {
            var wasVisible = indicator.IsVisible;
            indicator.IsVisible = false;
            _indicatorPlaced = false;
            _lastTarget = null;
            return wasVisible;
        }

        // 目标中心换算到覆盖层坐标系：列表滚动/侧栏收放后的实时几何都由此获得
        var center = target.TranslatePoint(new Point(0, target.Bounds.Height / 2), overlay);
        if (center is not { } point)
        {
            indicator.IsVisible = false;
            _indicatorPlaced = false;
            _lastTarget = null;
            return true;
        }

        // X 定位：展开态进入选中项内部左缘；收起态行内被图标占满，退回贴侧栏左缘。
        // 各目标项左缘一致，X 无需参与迁移动画（仅布局校正时直接吸附）
        var expanded = (DataContext as MainWindowViewModel)?.IsSidebarExpanded ?? true;
        var newX = expanded ? point.X + InsideItemInsetX : CollapsedEdgeX;

        var height = Math.Max(DotHeight, target.Bounds.Height - 8);
        var newTop = point.Y - DotHeight / 2;
        var newScale = DotHeight / height;

        // 迁移动画只在"选中目标变化"时播放；布局校正（初次落位、收起侧栏、滚动等）
        // 目标未变，直接吸附到最新几何，避免误播迁移
        var targetChanged = !ReferenceEquals(target, _lastTarget);
        if (_indicatorPlaced && targetChanged && NavIndicatorAnimationEnabled)
        {
            RunTransferAnimation(IndicatorTop + DotHeight / 2, point.Y, height);
        }

        indicator.IsVisible = true;
        var changed = !indicator.IsVisible
            || !NearEqual(indicator.Height, height)
            || !NearEqual(IndicatorTranslate.X, newX)
            || !NearEqual(IndicatorTranslate.Y, newTop)
            || !NearEqual(IndicatorScale.ScaleY, newScale);
        indicator.Height = height;
        IndicatorTranslate.X = newX;
        IndicatorTranslate.Y = newTop;
        IndicatorScale.ScaleY = newScale;
        IndicatorTop = newTop;
        IndicatorLeft = newX;
        IndicatorScaleY = newScale;
        _indicatorPlaced = true;
        _lastTarget = target;
        return changed;
    }

    /// <summary>初始布局序列里几何可能仍在变化（状态提示推挤列表等），空闲时复核一次；
    /// 仍在收敛则继续排队复核，稳定后自然停止，不会死循环。</summary>
    private void QueueIndicatorVerify()
    {
        if (_indicatorVerifyQueued)
        {
            return;
        }

        _indicatorVerifyQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _indicatorVerifyQueued = false;
                if (MoveNavIndicator())
                {
                    QueueIndicatorVerify();
                }
            },
            DispatcherPriority.SystemIdle);
    }

    /// <summary>双精度近似相等（指示点几何比较容差）。</summary>
    private static bool NearEqual(double a, double b) => Math.Abs(a - b) < 0.5;

    /// <summary>当前应显示指示点的侧栏元素：设置/关于入口按钮或选中游戏行。</summary>
    private Control? ResolveNavTarget()
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return null;
        }

        if (vm.IsSettingsNavActive)
        {
            return SettingsNavButton;
        }

        if (vm.IsAboutNavActive)
        {
            return AboutNavButton;
        }

        return vm.IsGameNavActive && vm.SelectedGame is { } game
            ? GamesList.ContainerFromItem(game)
            : null;
    }

    /// <summary>
    /// 迁移编舞（两段式）：①旧项上朝行进方向变长一倍（远端边固定）→ 45-55% 以 2 倍长形态
    /// 快速跳到新选中项（长度固定 2×点高，与行距无关，不会把两行连成一条）→
    /// ②在新疆界以逆向拉长形态落位并收缩回小点。平移与缩放各一条 keyframe 动画并行驱动
    /// RenderTransform 组内的对应子变换（目标必须是控件，TransformAnimator 才能找到子变换）。
    /// </summary>
    private void RunTransferAnimation(double oldCenter, double newCenter, double height)
    {
        _indicatorCts?.Cancel();
        _indicatorCts = new CancellationTokenSource();
        var ct = _indicatorCts.Token;

        var (translateValues, scaleValues) = BuildTransferCues(oldCenter, newCenter, height);
        var cues = new[] { 0.0, 0.45, 0.55, 1.0 };
        // 跳变段（帧1）线性：46ms 内原样平移，观感为"跳"而非"滑"
        var splines = new KeySpline?[] { EaseOutSpline, null, EaseOutSpline, null };
        var translate = new Animation { Duration = TransferDuration };
        var scale = new Animation { Duration = TransferDuration };
        for (var i = 0; i < cues.Length; i++)
        {
            translate.Children.Add(TransformCue(TranslateTransform.YProperty, cues[i], translateValues[i], splines[i]));
            scale.Children.Add(TransformCue(ScaleTransform.ScaleYProperty, cues[i], scaleValues[i], splines[i]));
        }

        PlayAsync(translate, NavIndicator, ct);
        PlayAsync(scale, NavIndicator, ct);
    }

    /// <summary>
    /// 计算迁移编舞的 4 组关键帧值（对应 0%/45%/55%/100%）。视觉区间为 [t, t+H·s]
    /// （组内先 Scale 后 Translate、原点 0,0）：向下切换时①顶沿固定向下变长一倍 → 跳到新项、
    /// 以向上探出 1 倍长的形态落位 → 收缩回小点；向上切换镜像（底沿固定向上变长）。
    /// 拉长长度固定为 2×点高，与两行间距无关。
    /// </summary>
    internal static (double[] Translate, double[] Scale) BuildTransferCues(
        double oldCenter, double newCenter, double elementHeight)
    {
        var top0 = oldCenter - DotHeight / 2;
        var top1 = newCenter - DotHeight / 2;
        var dotScale = DotHeight / elementHeight;
        var stretchedScale = DotHeight * 2 / elementHeight;
        var translate = newCenter < oldCenter
            ? new[] { top0, top0 - DotHeight, top1, top1 }      // 向上：底沿固定向上变长
            : new[] { top0, top0, top1 - DotHeight, top1 };     // 向下：顶沿固定向下变长
        return (translate, new[] { dotScale, stretchedScale, stretchedScale, dotScale });
    }

    /// <summary>构建单个 Transform 子属性 keyframe（默认线性；显式传入 KeySpline 的段落做平滑过渡，
    /// Avalonia 12 的 keyframe 缓动为 WPF 式贝塞尔控制点而非 Easing 对象）。</summary>
    private static KeyFrame TransformCue(AvaloniaProperty property, double cue, double value, KeySpline? spline = null)
    {
        var frame = new KeyFrame { Cue = new Cue(cue) };
        if (spline is not null)
        {
            frame.KeySpline = spline;
        }

        frame.Setters.Add(new Setter { Property = property, Value = value });
        return frame;
    }

    /// <summary>启动一条动画（Transform 同为 Animatable）；取消视为正常结束。</summary>
    private async void PlayAsync(Animation animation, Animatable target, CancellationToken ct)
    {
        try
        {
            await animation.RunAsync(target, ct);
        }
        catch (OperationCanceledException)
        {
            // 快速连点时被新一段动画接管，属预期
        }
    }
}
