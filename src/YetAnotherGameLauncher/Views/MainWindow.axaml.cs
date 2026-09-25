using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.Views;

/// <summary>
/// 主窗口代码后置：侧栏选中指示点动画。
/// 指示点是覆盖在侧栏上的共享 Border，展开态位于选中项内部左缘（收起态贴侧栏左缘——
/// 收起行内被图标占满，内置会压图标）；renderTransform 为 TransformGroup
/// （先 Scale 后 Translate、原点 0,0）→ 视觉区间 = [TranslateY, TranslateY+Height×ScaleY]。
/// 选中项变更时写入最终基值并启动手写驱动的两段式编舞：①旧项上朝行进方向变长一倍 →
/// 快速跳到新项 → ②以逆向拉长形态落位后收缩回小点（两段各自贴着新旧项，不把两行连成一条；
/// 驱动循环逐拍直写变换基值——不经过 Animation API，节拍由 Task.Delay 自泵，
/// 渲染循环空闲降频不影响；取消后残留中间值由新驱动首拍覆盖）。
/// </summary>
public partial class MainWindow : Window
{
    static MainWindow()
    {
        // 设置类输入框按 Enter 保存：TextBox 会把 Enter 标记为已处理，
        // 用类处理器（handledEventsToo）保证本窗口逻辑总能收到；按控件名过滤，
        // 其余输入框不受影响（DragEnter/DragOver 无此需求）。
        InputElement.KeyDownEvent.AddClassHandler<TextBox>(
            static (box, e) => HandleSettingsEnterSave(box, e),
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    /// <summary>设置类输入框按 Enter 保存：安装根目录（设置页）、代理地址（设置页）
    /// 与游戏安装目录（启动设置页）。</summary>
    private static void HandleSettingsEnterSave(TextBox box, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        switch (box.Name)
        {
            case "InstallRootBox" when box.DataContext is SettingsViewModel settings:
                settings.SaveInstallRootCommand.Execute(null);
                e.Handled = true;
                break;
            // 代理地址框与其他设置输入框的 Enter 保存行为一致（失败走 ProxySave 消息槽）
            case "ProxyAddressBox" when box.DataContext is SettingsViewModel proxy:
                proxy.SaveProxyCommand.Execute(null);
                e.Handled = true;
                break;
            // 游戏设置页的 DataContext 是其壳 GameSettingsViewModel（页面绑定以 LaunchSettings 开头），
            // 所以这里必须从壳上取 LaunchSettings——直接 is LaunchSettingsViewModel 永远不匹配
            case "InstallDirBox" when box.DataContext is GameSettingsViewModel gameSettings:
                gameSettings.LaunchSettings.SaveCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
    /// <summary>静态"小点"的视觉高度（px）。须小于最小的导航行高，保证静息时完整落在选中项内。</summary>
    internal const double DotHeight = 16;

    /// <summary>展开态下指示点相对选中项左缘的内缩距离（px）：完全进入项内、不压内容。</summary>
    private const double InsideItemInsetX = 3;

    /// <summary>收起态下指示点相对选中块左缘的内缩距离（px）：块 44 宽、图标盒 38 居中后
    /// 块内左缝仅 3px，点贴块左缘（与图标盒左缘相切、仅叠 2px）——与展开态同一"点在块内"语言
    /// （背景块不随 Item 负 Margin 扩展已实测，勿再尝试外扩块）。</summary>
    private const double CollapsedInsetX = 0;

    /// <summary>迁移动画总时长：0-45% 旧项上变长、45-55% 跳变、55-100% 新项上收缩。</summary>
    internal static readonly TimeSpan TransferDuration = TimeSpan.FromMilliseconds(420);

    /// <summary>在途迁移动画的取消源：快速连点时中断上一段；取消后残留中间值由新驱动首拍覆盖。</summary>
    private CancellationTokenSource? _indicatorCts;

    /// <summary>迁移驱动是否在途：为真时 MoveNavIndicator 只记录基值、不直写变换属性
    /// （飞行值由驱动独占写入，终态在驱动末拍收敛落位）。</summary>
    private bool _indicatorDriving;

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

        if (OperatingSystem.IsLinux())
        {
            // X11/XWayland 下合成器（Hyprland 等）会无视 BorderOnly 仍画 SSD 标题条，
            // 与应用自绘 chrome 形成双标题——Linux 显式去装饰（必须在 InitializeComponent 之后：
            // XAML 的 WindowDecorations 属性会在初始化时覆盖构造函数先写入的值）。
            // Windows 保持 BorderOnly（DWM 边框+阴影）。
            WindowDecorations = WindowDecorations.None;
        }
        GamesList.SelectionChanged += (_, _) => QueueIndicatorMove();
        GamesList.TemplateApplied += OnGamesListTemplateApplied;
        // 每次布局后校正落位：首次上屏时 Opened 触发点早于最终布局，几何需要收敛
        GamesList.LayoutUpdated += (_, _) => QueueIndicatorMove();
        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => QueueIndicatorMove(); // 首次上屏后按当前选中项直接落位
        PropertyChanged += OnWindowPropertyChanged; // 窗口状态 → 圆角/卡片边距与标题栏图标
        SizeChanged += OnWindowSizeChanged; // 窗口宽度 → 侧栏阈值自适应收放
        Closing += OnWindowClosing; // 关闭时持久化尺寸/最大化状态
        Closed += (_, _) =>
        {
            _indicatorCts?.Cancel(); // 窗口销毁即停驱动循环，不 orphan
            _splashCts?.Cancel(); // 启动遮蔽的脉动泵同理
        };
    }

    /// <summary>DataContext 就绪：注入持久化窗口状态的应用回调（目录加载完成时由 VM 调用）。</summary>
    private void ApplyPersistedWindowState(double width, double height, bool maximized)
    {
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
        if (maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>关闭时持久化窗口状态（先于窗口销毁读取 Width/Height；按"视觉最大化"记录，
    /// 实验性 Wayland 后端对平铺窗口的 Maximized 误报不应写进配置）；并停止背景视频解码——
    /// 解码循环必须先于平台拆除停下，退出期 GPU 解码栈失效会让继续解码向 stderr 刷错。</summary>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PersistWindowState(Width, Height, _lastVisualMaximized == true);
            viewModel.StopBackdropVideo();
        }
    }

    /// <summary>最近一次判定的视觉最大化（null=尚未判定）。状态与尺寸变化都触发重判：
    /// 后端先报 Maximized、客户区随后才铺开的序列中，两处时机缺一不可。</summary>
    private bool? _lastVisualMaximized;

    /// <summary>窗口状态变化：重判视觉最大化（标题栏图标与窗口外缘顶角圆角由该属性经绑定驱动；
    /// 内容卡左上圆角是内部角，不随最大化去除）。</summary>
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty)
        {
            return;
        }

        UpdateMaximizedFlag();
    }

    /// <summary>窗口尺寸变化：转发 VM 做侧栏阈值自适应（穿越阈值自动收放，宽度过渡自带动画）；
    /// 并重判视觉最大化——真最大化时后端先改状态、客户区尺寸随后才铺满工作区。</summary>
    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateMaximizedFlag();
        (DataContext as MainWindowViewModel)?.SetWindowWidth(e.NewSize.Width);
    }

    /// <summary>
    /// 依据窗口状态 + 客户区与屏幕工作区的吻合度重判"视觉最大化"并写入 VM。
    /// 实验性 Wayland 后端会把 Hyprland 的平铺状态也上报为 Maximized（2026-09 实测），
    /// 平铺/浮动窗口必须保留圆角，故不能只信属性（见 <see cref="WindowStateMapper"/>）。
    /// </summary>
    private void UpdateMaximizedFlag()
    {
        var screen = Screens?.ScreenFromWindow(this);
        var maximized = WindowStateMapper.IsVisuallyMaximized(
            WindowState, ClientSize, screen?.WorkingArea, screen?.Scaling);
        if (maximized == _lastVisualMaximized)
        {
            return;
        }

        _lastVisualMaximized = maximized;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.IsWindowMaximized = maximized;
        }
    }

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
            vm.WindowStateApplier = ApplyPersistedWindowState; // 目录加载完成后应用持久化窗口状态
            _hookedVm = vm;
            QueueIndicatorMove();
            SyncBootSplash(vm.IsBooting); // DataContext 后置：已处于启动遮蔽中的 VM（如截图导出）立即点亮遮蔽
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
        else if (e.PropertyName == nameof(MainWindowViewModel.IsBooting))
        {
            SyncBootSplash(((MainWindowViewModel?)sender)?.IsBooting == true);
        }
    }

    // ==================== 启动遮蔽层（IsBooting 驱动）：脉动条自泵 + 放行淡出 ====================

    /// <summary>测试开关：headless 会话不推进动画时钟，关闭后遮蔽放行即刻收起（放行逻辑本身不变）。</summary>
    internal bool SplashAnimationEnabled { get; set; } = true;

    /// <summary>脉动泵所有权守卫：遮蔽重入/窗口销毁时取消旧泵（快速连切不 orphan、不互踩）。</summary>
    private CancellationTokenSource? _splashCts;

    /// <summary>淡出代际守卫：放行瞬间若遮蔽又被开启（理论上不会，防御），旧淡出不得把新遮蔽收起。</summary>
    private int _splashFadeGeneration;

    /// <summary>脉动滑块位移（Transform 不能 x:Name（AVLN2000），运行时挂上；驱动循环独占写入）。</summary>
    private readonly TranslateTransform _splashPulseOffset = new();

    /// <summary>脉动滑块滑动行程（轨道 200 − 滑块 48；与 AXAML 常数保持同步）。</summary>
    private const double SplashPulseTravel = 200 - 48;

    /// <summary>脉动往返周期（秒）：三角波左右往返，观感即经典不定进度。</summary>
    private const double SplashPulsePeriodSeconds = 1.8;

    /// <summary>遮蔽放行淡出时长：就绪后遮蔽 0.25s 内让位（渲染循环空闲降频下自泵驱动，同迁移编舞）。</summary>
    private static readonly TimeSpan SplashFadeDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>脉动/淡出驱动节拍：~16ms 一拍，装饰性动画足够平滑且开销可忽略。</summary>
    private static readonly TimeSpan SplashTickInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>同步遮蔽层可见性（VM IsBooting 变化与 DataContext 就绪两处入口）。
    /// 门控轮询的 finally 在线程池线程撤 IsBooting，此处必须回 UI 线程再摸控件。</summary>
    private void SyncBootSplash(bool booting)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Post(() => SyncBootSplash(booting));
            return;
        }

        if (booting)
        {
            _splashFadeGeneration++; // 撤销在途淡出的所有权（若有）
            BootSplash.Opacity = 1;
            BootSplash.IsVisible = true;
            if (SplashAnimationEnabled)
            {
                StartSplashPulse();
            }
        }
        else
        {
            _splashCts?.Cancel();
            if (SplashAnimationEnabled && BootSplash.IsVisible)
            {
                DriveSplashFadeAsync(++_splashFadeGeneration);
            }
            else
            {
                BootSplash.IsVisible = false;
                BootSplash.Opacity = 1;
            }
        }
    }

    /// <summary>启动脉动泵（先取消在途泵）：滑块 Transform 只挂一次，位移由泵独占写入。</summary>
    private void StartSplashPulse()
    {
        _splashCts?.Cancel();
        var cts = new CancellationTokenSource();
        _splashCts = cts;
        SplashPulseBar.RenderTransform = _splashPulseOffset;
        DriveSplashPulseAsync(cts);
    }

    /// <summary>
    /// 脉动驱动循环：三角波往返写滑块位移基值。手写驱动而非 Animation API，与迁移编舞同理——
    /// 本应用渲染循环空闲时按需降频，Animation 时钟无外部泵无法自举；Task.Delay 循环自身即泵。
    /// </summary>
    private async void DriveSplashPulseAsync(CancellationTokenSource owner)
    {
        var ct = owner.Token;
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                // 三角波：0 → 行程 → 0（往返一个周期），线性即可
                var phase = stopwatch.Elapsed.TotalSeconds % SplashPulsePeriodSeconds / SplashPulsePeriodSeconds;
                var triangle = phase < 0.5 ? phase * 2 : (1 - phase) * 2;
                _splashPulseOffset.X = Math.Round(triangle * SplashPulseTravel, 1);
                await Task.Delay(SplashTickInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 放行/重入/窗口销毁取消，属预期
        }
        catch (Exception)
        {
            // async void 内未捕获异常会崩掉整个进程：装饰性动画失败静默兜住
        }
    }

    /// <summary>放行淡出：0.25s 线性淡出后收起并复位不透明度（终态写入基值，headless 可断言）。</summary>
    private async void DriveSplashFadeAsync(int generation)
    {
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var duration = SplashFadeDuration.TotalMilliseconds;
            while (true)
            {
                var progress = Math.Min(1, stopwatch.Elapsed.TotalMilliseconds / duration);
                BootSplash.Opacity = 1 - progress;
                if (progress >= 1)
                {
                    break;
                }

                await Task.Delay(SplashTickInterval);
                if (generation != _splashFadeGeneration)
                {
                    return; // 遮蔽被重新开启：新状态已接管可见性，旧淡出无权收起
                }
            }

            if (generation == _splashFadeGeneration)
            {
                BootSplash.IsVisible = false;
                BootSplash.Opacity = 1;
            }
        }
        catch (Exception)
        {
            // 装饰性动画失败兜住；保底收起，避免遮蔽永久钉在屏幕上
            if (generation == _splashFadeGeneration)
            {
                BootSplash.IsVisible = false;
                BootSplash.Opacity = 1;
            }
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

        // X 定位：两态都在选中块内部左缘（展开态内缩 3px、收起态 2px——块已外扩 56 居中，
        // 图标居中于侧栏中线，点与图标天然分离）。
        // 各目标块左缘一致系 DockPanel/底部 StackPanel 同为 12 的边距巧合，非结构不变量。
        // X 无需参与迁移动画（仅布局校正时直接吸附）
        var expanded = (DataContext as MainWindowViewModel)?.IsSidebarExpanded ?? true;
        var newX = point.X + (expanded ? InsideItemInsetX : CollapsedInsetX);

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

        // "此前隐藏→本次显示"也算变化：先读值再置可见（2026-09-20 复审修复——
        // 原实现先置 true 再读，!IsVisible 恒为 false 的死条件让该分支永远不触发）。
        // 几何比较必须用基值快照（IndicatorTop 等字段），不能读变换属性——动画进行中
        // 生效值被动画优先级覆盖，恒不等于终态，空闲复核会以每秒数万次自旋直到动画
        // 结束（2026-09-21 实测 141K 次/7s，纯自耗）
        var wasHidden = !indicator.IsVisible;
        indicator.IsVisible = true;
        var changed = wasHidden
            || !NearEqual(indicator.Height, height)
            || !NearEqual(IndicatorLeft, newX)
            || !NearEqual(IndicatorTop, newTop)
            || !NearEqual(IndicatorScaleY, newScale);
        // 迁移驱动进行中不直写平移/缩放（驱动独占写入飞行值），终态只入记录：
        // 手写驱动逐拍直写基值，此刻写入会把飞行值盖成终态（指示点先在目的地闪现
        // 再跳回，2026-09-21 实锤）；记录基值与飞行值的残差（按压缩放等几何漂移）
        // 由驱动末拍按实时几何收敛。Height 除外——驱动不写它，而渲染区间 = 高度×
        // 缩放，跨高度迁移（游戏行↔设置按钮）必须首拍就用新高度配新缩放
        indicator.Height = height;
        if (!_indicatorDriving)
        {
            IndicatorTranslate.X = newX;
            IndicatorTranslate.Y = newTop;
            IndicatorScale.ScaleY = newScale;
        }
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
    /// ②在新疆界以逆向拉长形态落位并收缩回小点。先取消在途驱动再启动新驱动。
    /// 手写驱动而非 Animation API：本应用的渲染循环空闲时按需降频（实测 ~10Hz），
    /// Animation 时钟取自渲染循环、无外部泵时无法自举——旧版复核环的每秒数万次
    /// 分发器投递曾意外充当渲染泵（2026-09-21 实测：泵移除后两方向动画都掉到 ~10fps）。
    /// Task.Delay 循环自身就是泵：每拍写变换基值 → 失效 → 渲染，节拍不依赖渲染循环。
    /// </summary>
    private void RunTransferAnimation(double oldCenter, double newCenter, double height)
    {
        _indicatorCts?.Cancel();
        var cts = new CancellationTokenSource();
        _indicatorCts = cts;
        _indicatorDriving = true;

        DriveTransferAsync(oldCenter, newCenter, height, cts);
    }

    /// <summary>驱动节拍：~8ms 一拍（60-125fps），足以平滑且开销可忽略（每次迁移 ≤53 拍）。</summary>
    private static readonly TimeSpan TransferTickInterval = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// 迁移驱动循环：按墙钟进度逐拍把插值结果写进变换基值；完成/取消/被替换即退出。
    /// 所有权以 <c>_indicatorCts</c> 比对判定：快速连点时旧驱动的收尾（异常复位/收敛）
    /// 不得覆盖新驱动的飞行值。编舞时间线与旧 Animation 引擎实测行为一致
    /// （生长/收缩线性、跳变段缓出减速入位）。
    /// </summary>
    private async void DriveTransferAsync(double oldCenter, double newCenter, double height,
        CancellationTokenSource owner)
    {
        var ct = owner.Token;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var duration = TransferDuration.TotalMilliseconds;
            while (true)
            {
                var progress = sw.Elapsed.TotalMilliseconds / duration;
                var (translateY, scaleY) = EvalTransfer(oldCenter, newCenter, height, progress);
                IndicatorTranslate.Y = translateY;
                IndicatorScale.ScaleY = scaleY;
                if (progress >= 1)
                {
                    break;
                }

                await Task.Delay(TransferTickInterval, ct);
            }

            // 末拍收敛：迁移起点几何可能带着按压时的 RenderTransform 缩放（pressable 按下
            // scale(0.97) 参与 TranslatePoint），飞行期间布局也可能微调——按实时几何重算
            // 并吸附，残差在落地帧内吃掉，不会推迟到下一次指针活动才补写（旧症状：
            // 动画结束、鼠标移开时指示点又挪一下）
            if (ReferenceEquals(_indicatorCts, owner))
            {
                _indicatorDriving = false;
                MoveNavIndicator();
            }
        }
        catch (OperationCanceledException)
        {
            // 快速连点时被新一段驱动接管，属预期；残留中间值由新驱动首拍覆盖
        }
        catch (Exception)
        {
            // async void 内未捕获异常会崩掉整个进程：动画失败是纯装饰性问题，兜住。
            // 仅在仍持有所有权时按记录基值复位，别把新驱动的飞行值拉回旧终态
            if (ReferenceEquals(_indicatorCts, owner))
            {
                IndicatorTranslate.X = IndicatorLeft;
                IndicatorTranslate.Y = IndicatorTop;
                IndicatorScale.ScaleY = IndicatorScaleY;
            }
        }
        finally
        {
            if (ReferenceEquals(_indicatorCts, owner))
            {
                _indicatorDriving = false;
                _indicatorCts = null; // 摘除已完成的所有权引用，窗口关闭的 Cancel 对 null 安全
            }
        }
    }

    /// <summary>
    /// 迁移编舞插值（纯函数）：progress ∈ [0,1] → (TranslateY, ScaleY)，越界钳到端点。
    /// 段落曲线：0-45% 生长线性、45-55% 跳变缓出（快速起步减速入位）、55-100% 收缩线性。
    /// </summary>
    internal static (double TranslateY, double ScaleY) EvalTransfer(
        double oldCenter, double newCenter, double elementHeight, double progress)
    {
        var (t, s) = BuildTransferCues(oldCenter, newCenter, elementHeight);
        var p = Math.Clamp(progress, 0, 1);
        if (p < 0.45)
        {
            var u = p / 0.45;
            return (Lerp(t[0], t[1], u), Lerp(s[0], s[1], u));
        }

        if (p <= 0.55)
        {
            var u = EaseOutHop((p - 0.45) / 0.10);
            return (Lerp(t[1], t[2], u), s[1]);
        }

        var v = (p - 0.55) / 0.45;
        return (Lerp(t[2], t[3], v), Lerp(s[2], s[3], v));
    }

    /// <summary>线性插值。</summary>
    private static double Lerp(double from, double to, double u) => from + (to - from) * u;

    /// <summary>
    /// 跳变段缓出：三次贝塞尔 (0,0)-(0.58,1) 的缓动曲线（对齐旧引擎 KeySpline 行为——
    /// 输入均匀时间 x，解曲线参数 t 再取 y）。二分 24 拍，误差远小于亚像素。
    /// </summary>
    private static double EaseOutHop(double x)
    {
        var lo = 0.0;
        var hi = 1.0;
        for (var i = 0; i < 24; i++)
        {
            var mid = (lo + hi) / 2;
            var bezierX = 3 * (1 - mid) * mid * mid * 0.58 + mid * mid * mid;
            if (bezierX < x)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        var t = (lo + hi) / 2;
        return 3 * (1 - t) * t * t + t * t * t;
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
}
