using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 无头窗口验证侧栏导航高亮与切换动画（等价于此前的人工真机验证，可重复回归）：
/// 页面状态、TwoWay 绑定链路、模拟点击、动画时钟推进。
/// </summary>
[Collection("sequential")]
public class SidebarNavHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public SidebarNavHeadlessTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task SettingsPage_ClearsGameListSelection_AndHighlightsSettingsNav()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();

            var list = window.FindControl<ListBox>("GamesList");
            Assert.Same(_ctx.Vm.Games[0], list!.SelectedItem); // 前置：详情页游戏在列表中高亮

            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();

            // 转发属性的 null 经 TwoWay 绑定真实推到了 ListBox
            Assert.Null(list.SelectedItem);

            // 侧栏底部导航按钮按文档顺序 = 设置、关于
            var navButtons = window.GetVisualDescendants()
                .OfType<Button>().Where(b => b.Classes.Contains("nav-item")).ToList();
            Assert.Equal(2, navButtons.Count);
            Assert.Contains("selected", navButtons[0].Classes);  // 设置入口高亮
            Assert.DoesNotContain("selected", navButtons[1].Classes); // 关于入口不高亮
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AboutPage_HighlightsAboutNavOnly()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            _ctx.Vm.ShowAboutCommand.Execute(null);
            window.UpdateLayout();

            var navButtons = window.GetVisualDescendants()
                .OfType<Button>().Where(b => b.Classes.Contains("nav-item")).ToList();
            Assert.DoesNotContain("selected", navButtons[0].Classes);
            Assert.Contains("selected", navButtons[1].Classes); // 关于入口高亮
            Assert.Null(window.FindControl<ListBox>("GamesList")!.SelectedItem);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GameSettingsPage_KeepsGameSelectedInSidebar()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();

            Assert.Same(_ctx.Vm.Games[0], window.FindControl<ListBox>("GamesList")!.SelectedItem);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ClickingCurrentGameRow_FromSettings_NavigatesBackToGame()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();

            // 模拟点击列表第一行（仍是"当前游戏"）：ListBox 选中未变化，
            // 走 GameNavSelection 转发属性导航回详情页——纯 VM 测试覆盖不到的绑定链路
            var row = window.GetVisualDescendants().OfType<ListBoxItem>().First();
            var center = row.TranslatePoint(
                new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            window.UpdateLayout();

            Assert.Same(_ctx.Vm.Games[0], _ctx.Vm.CurrentPage);
            Assert.Same(_ctx.Vm.Games[0], window.FindControl<ListBox>("GamesList")!.SelectedItem);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PageTransition_DirectionClassesFlip_AndAnimationSettles()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();

            var host = window.FindControl<ContentControl>("PageHost")!;
            Assert.Contains("nav-forward", host.Classes);
            Assert.DoesNotContain("nav-back", host.Classes);

            // 方向类随导航翻转；样式动画本身在 headless 会话中不执行（已实测探针证实，
            // 动画观感与终态由截图 + 视觉验收覆盖），这里锁定的是驱动动画的方向状态
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            Assert.Contains("nav-forward", host.Classes); // 前进：自右滑入

            _ctx.Vm.ShowGamesCommand.Execute(null);
            window.UpdateLayout();
            Assert.Contains("nav-back", host.Classes); // 后退：自左滑入
            Assert.DoesNotContain("nav-forward", host.Classes);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NavIndicator_PlacesAtSelectedGameRow_AndFollowsSelection()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            // headless 动画冻结在首帧：关闭迁移动画后基值即终态，断言几何落位逻辑
            window.NavIndicatorAnimationEnabled = false;
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs(); // 冲刷 Background 优先级的落位任务

            var indicator = window.FindControl<Border>("NavIndicator")!;
            var overlay = (Panel)indicator.Parent!;
            var rows = window.GetVisualDescendants().OfType<ListBoxItem>().ToList();

            // 静止形态：可见小点，渲染中心对齐选中行中心，且完整落在选中行内部
            Assert.True(indicator.IsVisible);
            Assert.Equal(rows[0].Bounds.Height - 8, indicator.Height, 1);
            Assert.Equal(MainWindow.DotHeight / indicator.Height, window.IndicatorScaleY, 2);
            Assert.Equal(RowCenterY(rows[0], overlay), RenderedCenterY(window, indicator), 1);
            AssertRestingInsideTarget(window, indicator, rows[0], overlay);

            // 选中第二个游戏：指示点渲染中心落到第二行
            _ctx.Vm.GameNavSelection = _ctx.Vm.Games[1];
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(RowCenterY(rows[1], overlay), RenderedCenterY(window, indicator), 1);
            AssertRestingInsideTarget(window, indicator, rows[1], overlay);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NavIndicator_DuringTransfer_TransformStaysAtOldPosition_NoDestinationFlash()
    {
        // 手写驱动回归（2026-09-21 实锤）：启动迁移后，终态基值不得立即写进变换属性——
        // 驱动直写基值（没有动画优先级层遮盖），此刻写入会把飞行值盖成终态，
        // 观感为指示点先在目的地闪现再跳回旧项。headless 下驱动停在首拍
        // （Task.Delay 是真实墙钟），变换值应钉在旧项中心。
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var indicator = window.FindControl<Border>("NavIndicator")!;
            var overlay = (Panel)indicator.Parent!;
            var rows = window.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            var oldCenter = RowCenterY(rows[0], overlay);
            var newCenter = RowCenterY(rows[1], overlay);

            _ctx.Vm.GameNavSelection = _ctx.Vm.Games[1];
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs(); // 驱动首拍同步写入旧项位置

            var rendered = ((TransformGroup)indicator.RenderTransform!).Value
                .Transform(new Point(indicator.Width / 2, indicator.Height / 2));
            Assert.Equal(oldCenter, rendered.Y, 1);        // 钉在旧项，不是新项（闪现）
            Assert.True(Math.Abs(rendered.Y - newCenter) > 5, "迁移起步不应已在目的地");
            window.Close(); // Closed 取消驱动循环
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NavIndicator_FollowsSettingsAndAboutEntries()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.NavIndicatorAnimationEnabled = false; // 同上：直接断言落位
            window.Show();
            window.UpdateLayout();

            var indicator = window.FindControl<Border>("NavIndicator")!;
            var overlay = (Panel)indicator.Parent!;
            var navButtons = window.GetVisualDescendants()
                .OfType<Button>().Where(b => b.Classes.Contains("nav-item")).ToList();

            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.True(indicator.IsVisible);
            Assert.Equal(ButtonCenterY(navButtons[0], overlay), RenderedCenterY(window, indicator), 1);
            AssertRestingInsideTarget(window, indicator, navButtons[0], overlay);

            _ctx.Vm.ShowAboutCommand.Execute(null);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ButtonCenterY(navButtons[1], overlay), RenderedCenterY(window, indicator), 1);
            AssertRestingInsideTarget(window, indicator, navButtons[1], overlay);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NavIndicator_RecomputesWhenSidebarCollapses()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.NavIndicatorAnimationEnabled = false;
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var indicator = window.FindControl<Border>("NavIndicator")!;
            var overlay = (Panel)indicator.Parent!;
            var expandedHeight = indicator.Height;

            // 收起侧栏：行内文本隐藏 + 内边距收窄，行高变小，指示点随之重算
            _ctx.Vm.ToggleSidebarCommand.Execute(null);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var row = window.GetVisualDescendants().OfType<ListBoxItem>().First();
            Assert.True(indicator.IsVisible);
            Assert.Equal(row.Bounds.Height - 8, indicator.Height, 1);
            Assert.True(indicator.Height < expandedHeight);
            Assert.Equal(RowCenterY(row, overlay), RenderedCenterY(window, indicator), 1);
            // 收起态：行内被图标占满，指示点退到项背景左缘外侧（点右缘与项左缘留 2px 间隙），
            // 必须跟随目标项几何——贴窗口边的旧形态与项背景脱开，观感"悬空"（2026-09-25 修复）
            var rowLeft = row.TranslatePoint(new Point(0, 0), overlay)!.Value.X;
            Assert.Equal(rowLeft - indicator.Width - 2, window.IndicatorLeft, 1);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NavIndicator_CollapsedCentersIcons_AndAnchorsDotToNavButton()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.NavIndicatorAnimationEnabled = false;
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            _ctx.Vm.ToggleSidebarCommand.Execute(null);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var indicator = window.FindControl<Border>("NavIndicator")!;
            var overlay = (Panel)indicator.Parent!;

            // 收起态所有图标居中于侧栏中线：游戏行内边距 3、导航钮内边距 8。
            // 中线取 VM 逻辑宽度（headless 下宽度过渡冻结在展开值，overlay 实时宽度不可用；
            // 左锚定几何不受冻结影响）。导航钮本地 Padding 压制收缩覆盖的旧形态已移除
            var sidebarMidline = _ctx.Vm.SidebarWidth / 2.0;
            var row = window.GetVisualDescendants().OfType<ListBoxItem>().First();
            var rowIcon = row.GetVisualDescendants().OfType<Border>()
                .First(b => Math.Abs(b.Bounds.Width - 38) < 0.5 && Math.Abs(b.Bounds.Height - 38) < 0.5);
            Assert.Equal(sidebarMidline, rowIcon.TranslatePoint(new Point(19, 19), overlay)!.Value.X, 1);

            // 设置入口：图标在按钮内水平居中（旧形态本地 Left 对齐→图标偏左），
            // 指示点对齐按钮中心 Y、贴按钮左缘外侧（与游戏行同一条几何规则）
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var settings = window.FindControl<Button>("SettingsNavButton")!;
            var settingsIcon = settings.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First();
            var iconCenterInButton = settingsIcon.TranslatePoint(new Point(9, 9), settings)!.Value.X;
            // 布局取整会把居中结果偏 0.5px（宽度 44/45 抖动），断言用 ±0.75 容差
            Assert.InRange(iconCenterInButton, settings.Bounds.Width / 2 - 0.75, settings.Bounds.Width / 2 + 0.75);
            Assert.Equal(ButtonCenterY(settings, overlay), RenderedCenterY(window, indicator), 1);
            var buttonLeft = settings.TranslatePoint(new Point(0, 0), overlay)!.Value.X;
            Assert.Equal(buttonLeft - indicator.Width - 2, window.IndicatorLeft, 1);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NavIndicator_RapidSwitches_SettleOnFinalTarget()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.NavIndicatorAnimationEnabled = false;
            window.Show();
            window.UpdateLayout();

            var indicator = window.FindControl<Border>("NavIndicator")!;
            var overlay = (Panel)indicator.Parent!;
            var rows = window.GetVisualDescendants().OfType<ListBoxItem>().ToList();

            // 快速连点：在途动画被取消接管，最终落位必须正确
            _ctx.Vm.GameNavSelection = _ctx.Vm.Games[1];
            _ctx.Vm.GameNavSelection = _ctx.Vm.Games[0];
            _ctx.Vm.GameNavSelection = _ctx.Vm.Games[1];
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.True(indicator.IsVisible);
            Assert.Equal(RowCenterY(rows[1], overlay), RenderedCenterY(window, indicator), 1);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public void TransferCues_TwoPhase_GrowOnOldItem_Hop_RetractOnNewItem()
    {
        const double height = 46;
        const double oldCenter = 200;
        const double gap = 100;
        var dot = MainWindow.DotHeight;
        var top0 = oldCenter - dot / 2; // 静息小点顶边
        var s0 = dot / height;
        var s1 = dot * 2 / height;

        // 向下切：①顶沿固定向下变长一倍 → 跳到新项（向上探出一倍形态落位）→ 收缩
        var (downT, downS) = MainWindow.BuildTransferCues(oldCenter, oldCenter + gap, height);
        // 向上切（镜像）：底沿固定向上变长
        var (upT, upS) = MainWindow.BuildTransferCues(oldCenter, oldCenter - gap, height);

        var expectedDownT = new[] { top0, top0, top0 + gap - dot, top0 + gap };
        var expectedUpT = new[] { top0, top0 - dot, top0 - gap, top0 - gap };
        var downSpans = new[] { (top0, top0 + 16), (top0, top0 + 32), (top0 + 84, top0 + 116), (top0 + 100, top0 + 116) };
        var upSpans = new[] { (top0, top0 + 16), (top0 - 16, top0 + 16), (top0 - 100, top0 - 68), (top0 - 100, top0 - 84) };
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(expectedDownT[i], downT[i], 6);
            Assert.Equal(expectedUpT[i], upT[i], 6);
            var expectedScale = i is 1 or 2 ? s1 : s0; // 两端小点，中间 2 倍长
            Assert.Equal(expectedScale, downS[i], 6);
            Assert.Equal(expectedScale, upS[i], 6);
            // 渲染区间 [t, t+H·s]：每帧长度 ≤ 2×点高——任何帧都不会把两行连成一条
            Assert.Equal(downSpans[i].Item1, downT[i], 6);
            Assert.Equal(downSpans[i].Item2, downT[i] + height * downS[i], 6);
            Assert.Equal(upSpans[i].Item1, upT[i], 6);
            Assert.Equal(upSpans[i].Item2, upT[i] + height * upS[i], 6);
        }
    }

    [Fact]
    public void TransferEval_PinsEdges_PhasesInOrder_AndStaysContinuous()
    {
        const double height = 46;
        const double oldCenter = 200;
        const double gap = 100;
        var dot = MainWindow.DotHeight;
        var top0 = oldCenter - dot / 2;

        foreach (var (name, newCenter) in new[] { ("down", oldCenter + gap), ("up", oldCenter - gap) })
        {
            var top1 = newCenter - dot / 2;
            var s0 = dot / height;
            var s1 = dot * 2 / height;

            // 端点即静息形态（Lerp 舍入差 <1ulp，用容差而非元组全等）
            var start = MainWindow.EvalTransfer(oldCenter, newCenter, height, 0);
            var end = MainWindow.EvalTransfer(oldCenter, newCenter, height, 1);
            Assert.Equal(top0, start.TranslateY, 6);
            Assert.Equal(s0, start.ScaleY, 6);
            Assert.Equal(top1, end.TranslateY, 6);
            Assert.Equal(s0, end.ScaleY, 6);
            // 越界钳到端点（驱动循环末拍可能因节拍粒度略超 1）
            var over = MainWindow.EvalTransfer(oldCenter, newCenter, height, 1.3);
            Assert.Equal(end.TranslateY, over.TranslateY, 6);
            Assert.Equal(end.ScaleY, over.ScaleY, 6);

            (double Ty, double Sy) prev = default;
            for (var i = 0; i <= 200; i++)
            {
                var p = i / 200.0;
                var (ty, sy) = MainWindow.EvalTransfer(oldCenter, newCenter, height, p);
                var top = ty;
                var bottom = ty + height * sy;

                // 任意时刻长度 ≤ 2×点高：不把两行连成一条
                Assert.True(bottom - top <= dot * 2 + 0.001, $"{name} p={p} span={bottom - top}");
                // 生长段（0-45%）：远端边钉在旧项（向下=顶沿，向上=底沿）
                if (p is > 0 and < 0.45)
                {
                    if (name == "down")
                    {
                        Assert.Equal(top0, top, 0.001);
                    }
                    else
                    {
                        Assert.Equal(oldCenter + dot / 2, bottom, 0.001);
                    }
                }
                // 跳变段（45-55%）：保持 2 倍长形态平移
                else if (p is >= 0.45 and <= 0.55)
                {
                    Assert.Equal(s1, sy, 6);
                }
                // 收缩段（55-100%）：近端边钉在新项（向下=底沿，向上=顶沿）
                else if (p > 0.55)
                {
                    if (name == "down")
                    {
                        Assert.Equal(newCenter + dot / 2, bottom, 0.001);
                    }
                    else
                    {
                        Assert.Equal(top1, top, 0.001);
                    }
                }

                // 逐拍连续：段内与段接缝都不允许像素级跳变（缓动只改速度不改值域）
                if (i > 0)
                {
                    Assert.True(Math.Abs(ty - prev.Ty) < dot, $"{name} p={p} translate jump");
                    Assert.True(Math.Abs(sy - prev.Sy) < 0.1, $"{name} p={p} scale jump");
                }

                prev = (ty, sy);
            }
        }
    }

    [Fact]
    public void TransferEval_Hop_IsEasedOut_FastStartDeceleratingLanding()
    {
        const double height = 46;
        const double oldCenter = 200;
        const double gap = 100;

        // 跳变段缓出（贝塞尔 0,0,0.58,1）：起步快、入位减速——
        // 时间中点的进度应超过线性中点，且前 1/5 段位移大于后 1/5 段
        var mid = MainWindow.EvalTransfer(oldCenter, oldCenter - gap, height, 0.5).TranslateY;
        var linearMid = (oldCenter - 16) + ((oldCenter - gap) - (oldCenter - 16)) * 0.5;
        Assert.True(mid < linearMid, $"up hop mid {mid} should overshoot linear {linearMid}");

        var early = Math.Abs(MainWindow.EvalTransfer(oldCenter, oldCenter - gap, height, 0.46).TranslateY
            - MainWindow.EvalTransfer(oldCenter, oldCenter - gap, height, 0.45).TranslateY);
        var late = Math.Abs(MainWindow.EvalTransfer(oldCenter, oldCenter - gap, height, 0.55).TranslateY
            - MainWindow.EvalTransfer(oldCenter, oldCenter - gap, height, 0.54).TranslateY);
        Assert.True(early > late, $"hop should decelerate: early={early} late={late}");
    }

    [Fact]
    public async Task CustomTitleBar_ButtonsPresent_AndMaximizeIconToggles()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            // 视觉最大化判定要求客户区铺满工作区（防 Wayland 后端把平铺误报成 Maximized）：
            // headless 的平台窗口只在 Show 前接受尺寸（UiScreenshotTests 同款手法），
            // 且不会随 Maximized 状态自动铺满（真合成器行为），构造时即按工作区定尺寸。
            var window = new MainWindow { DataContext = _ctx.Vm };
            var headlessScreen = window.Screens!.ScreenFromWindow(window)!;
            window.Width = headlessScreen.WorkingArea.Width / headlessScreen.Scaling;
            window.Height = headlessScreen.WorkingArea.Height / headlessScreen.Scaling;
            window.Show();
            window.UpdateLayout();

            Assert.NotNull(window.FindControl<Button>("CaptionMinimize"));
            Assert.NotNull(window.FindControl<Button>("CaptionMaximize"));
            Assert.NotNull(window.FindControl<Button>("CaptionClose"));
            Assert.NotNull(window.FindControl<Border>("ContentCard"));
            Assert.NotNull(window.FindControl<Border>("TitleChrome"));

            // 最大化：标题钮图标切换为还原；窗口外缘顶角去圆角，
            // 但内容卡左上角是侧栏与内容卡之间的内部角（不贴屏幕边）——最大化也保留
            window.WindowState = WindowState.Maximized;
            window.UpdateLayout();
            Assert.True(window.FindControl<Control>("RestoreIcon")!.IsVisible);
            Assert.False(window.FindControl<Control>("MaximizeIcon")!.IsVisible);
            Assert.Equal(new CornerRadius(10, 0, 0, 0), window.FindControl<Border>("ContentCard")!.CornerRadius);
            Assert.Equal(new CornerRadius(0), window.FindControl<Border>("TitleChrome")!.CornerRadius);

            // 还原：图标切回最大化
            window.WindowState = WindowState.Normal;
            window.UpdateLayout();
            Assert.False(window.FindControl<Control>("RestoreIcon")!.IsVisible);
            Assert.True(window.FindControl<Control>("MaximizeIcon")!.IsVisible);
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>
    /// 读取指示点的"渲染合成中心 Y"：用实际变换矩阵变换元素中心点。
    /// 首个版本的 bug 是基值正确但组内子顺序（先 Translate 后 Scale）导致渲染错位——
    /// 矩阵断言防该类回归；同时校验内部状态与矩阵一致。
    /// </summary>
    private static double RenderedCenterY(MainWindow window, Border indicator)
    {
        var matrix = ((TransformGroup)indicator.RenderTransform!).Value;
        var rendered = matrix.Transform(new Point(indicator.Width / 2, indicator.Height / 2));
        Assert.Equal(window.IndicatorTop + window.IndicatorScaleY * indicator.Height / 2, rendered.Y, 1);
        return rendered.Y;
    }

    /// <summary>
    /// 断言静息指示点的视觉矩形（[Top, Top+DotHeight] × [Left, Left+Width]）完整落在目标项内——
    /// 指示点进入选中项内部后防止溢出/压线的回归防线。
    /// </summary>
    private static void AssertRestingInsideTarget(
        MainWindow window, Border indicator, Control target, Visual overlay)
    {
        var origin = target.TranslatePoint(new Point(0, 0), overlay)!.Value;
        Assert.InRange(window.IndicatorTop, origin.Y, origin.Y + target.Bounds.Height - MainWindow.DotHeight);
        Assert.InRange(window.IndicatorLeft, origin.X, origin.X + target.Bounds.Width - indicator.Width);
    }

    /// <summary>列表行中心在指示点覆盖层坐标系下的 Y。</summary>
    private static double RowCenterY(ListBoxItem row, Visual overlay) =>
        row.TranslatePoint(new Point(0, row.Bounds.Height / 2), overlay)!.Value.Y;

    /// <summary>导航按钮中心在指示点覆盖层坐标系下的 Y。</summary>
    private static double ButtonCenterY(Button button, Visual overlay) =>
        button.TranslatePoint(new Point(0, button.Bounds.Height / 2), overlay)!.Value.Y;
}
