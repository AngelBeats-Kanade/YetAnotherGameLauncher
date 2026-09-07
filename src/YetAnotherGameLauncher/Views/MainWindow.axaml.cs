using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.Views;

/// <summary>
/// 主窗口代码后置：侧栏选中指示点动画。
/// 指示点是覆盖在侧栏左缘的共享 Border：位置走 TranslateTransform.Y、"小点 ↔ 长条"
/// 形态走 ScaleTransform.ScaleY（RenderTransformOrigin 0.5,0.5 使缩放以自身中心为准）。
/// 选中项变更时写入最终基值并播放"拉长 → 平移 → 缩短"迁移动画——
/// 动画进行中动画值覆盖基值，结束后回落到基值即终态（headless 会话不执行动画，
/// 基值始终可见，测试因此可直接断言落位）。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>静态"小点"的视觉高度（px）。</summary>
    private const double DotHeight = 10;

    /// <summary>迁移动画总时长：0-35% 原位拉长、35-65% 平移、65-100% 缩短。</summary>
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

    // XAML 编译器只给控件生成 x:Name 字段，Transform 需按 AXAML 中的声明顺序解析
    private TranslateTransform IndicatorTranslate =>
        (TranslateTransform)((TransformGroup)NavIndicator.RenderTransform!).Children[0];

    private ScaleTransform IndicatorScale =>
        (ScaleTransform)((TransformGroup)NavIndicator.RenderTransform!).Children[1];

    public MainWindow()
    {
        InitializeComponent();
        GamesList.SelectionChanged += (_, _) => QueueIndicatorMove();
        GamesList.TemplateApplied += OnGamesListTemplateApplied;
        // 每次布局后校正落位：首次上屏时 Opened 触发点早于最终布局，几何需要收敛
        GamesList.LayoutUpdated += (_, _) => QueueIndicatorMove();
        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => QueueIndicatorMove(); // 首次上屏后按当前选中项直接落位
    }

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

        var height = Math.Max(DotHeight, target.Bounds.Height - 8);
        var newTop = point.Y - height / 2;
        var newScale = DotHeight / height;

        // 迁移动画只在"选中目标变化"时播放；布局校正（初次落位、收起侧栏、滚动等）
        // 目标未变，直接吸附到最新几何，避免误播迁移
        var targetChanged = !ReferenceEquals(target, _lastTarget);
        var oldTop = IndicatorTranslate.Y;
        var oldScale = IndicatorScale.ScaleY;
        if (_indicatorPlaced && targetChanged && NavIndicatorAnimationEnabled)
        {
            RunTransferAnimation(oldTop, newTop, oldScale, newScale);
        }

        indicator.IsVisible = true;
        var changed = !indicator.IsVisible
            || !NearEqual(indicator.Height, height)
            || !NearEqual(IndicatorTranslate.Y, newTop)
            || !NearEqual(IndicatorScale.ScaleY, newScale);
        indicator.Height = height;
        IndicatorTranslate.Y = newTop;
        IndicatorScale.ScaleY = newScale;
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
    /// 迁移动画：0-35% 在原位拉长（ScaleY 小点 → 1）、35-65% 平移到新选中项（EaseInOut）、
    /// 65-100% 缩短回小点。Transform 动画的目标必须是控件本身（TransformAnimator
    /// 会在其 RenderTransform 组内按属性属主类型找到子 Transform 驱动），
    /// 平移与缩放各一条动画并行播放。
    /// </summary>
    private void RunTransferAnimation(double oldTop, double newTop, double oldScale, double newScale)
    {
        _indicatorCts?.Cancel();
        _indicatorCts = new CancellationTokenSource();
        var ct = _indicatorCts.Token;

        var translate = new Animation
        {
            Duration = TransferDuration,
            Children =
            {
                TransformCue(TranslateTransform.YProperty, 0, oldTop),
                TransformCue(TranslateTransform.YProperty, 0.35, oldTop),
                TransformCue(TranslateTransform.YProperty, 0.65, newTop, EaseInOutSpline),
                TransformCue(TranslateTransform.YProperty, 1, newTop),
            },
        };
        var scale = new Animation
        {
            Duration = TransferDuration,
            Children =
            {
                TransformCue(ScaleTransform.ScaleYProperty, 0, oldScale),
                TransformCue(ScaleTransform.ScaleYProperty, 0.35, 1, EaseOutSpline),
                TransformCue(ScaleTransform.ScaleYProperty, 0.65, 1),
                TransformCue(ScaleTransform.ScaleYProperty, 1, newScale, EaseOutSpline),
            },
        };
        PlayAsync(translate, NavIndicator, ct);
        PlayAsync(scale, NavIndicator, ct);
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
