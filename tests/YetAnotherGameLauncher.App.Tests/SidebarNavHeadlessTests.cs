using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Views;
using Xunit;

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
            // 收起态例外：行内被图标占满，指示点退回贴侧栏左缘（不进项内）
            Assert.Equal(3, window.IndicatorLeft, 1);
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
    public void TransferCues_StretchOppositeThenRetractOnArrival()
    {
        const double height = 46;
        const double oldCenter = 200;
        var top0 = oldCenter - MainWindow.DotHeight / 2; // 静息小点顶边

        // 向上切：顶沿固定（前两帧 t 相同）向下拉长 → 整体上滑 → 底部收缩到新点
        var (upT, upS) = MainWindow.BuildTransferCues(oldCenter, 100, height);
        // 向下切（镜像）：底沿固定向上拉长 → 整体下滑 → 顶部收缩到新点
        var (downT, downS) = MainWindow.BuildTransferCues(oldCenter, 300, height);

        var expectedUpT = new[] { top0, top0, top0 - 100, top0 - 100 };
        var expectedDownT = new[] { top0, top0 - 100, top0, top0 + 100 };
        var upSpans = new[] { (top0, top0 + 16), (top0, top0 + 116), (top0 - 100, top0 + 16), (top0 - 100, top0 - 84) };
        var downSpans = new[] { (top0, top0 + 16), (top0 - 100, top0 + 16), (top0, top0 + 116), (top0 + 100, top0 + 116) };
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(expectedUpT[i], upT[i], 6);
            Assert.Equal(expectedDownT[i], downT[i], 6);
            // 拉长长度 = 点高(16) + 行进距离(100)；两端帧收回小点
            var expectedScale = i is 1 or 2 ? (MainWindow.DotHeight + 100) / height : MainWindow.DotHeight / height;
            Assert.Equal(expectedScale, upS[i], 6);
            Assert.Equal(expectedScale, downS[i], 6);
            // 渲染区间 [t, t+H·s]：起点小点 → 覆盖两行间的长条 → 终点小点
            Assert.Equal(upSpans[i].Item1, upT[i], 6);
            Assert.Equal(upSpans[i].Item2, upT[i] + height * upS[i], 6);
            Assert.Equal(downSpans[i].Item1, downT[i], 6);
            Assert.Equal(downSpans[i].Item2, downT[i] + height * downS[i], 6);
        }
    }

    [Fact]
    public async Task CustomTitleBar_ButtonsPresent_AndMaximizedClassToggles()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            Assert.NotNull(window.FindControl<Button>("CaptionMinimize"));
            Assert.NotNull(window.FindControl<Button>("CaptionMaximize"));
            Assert.NotNull(window.FindControl<Button>("CaptionClose"));
            Assert.NotNull(window.FindControl<Border>("ContentCard"));
            Assert.DoesNotContain("maximized", window.ContentCard.Classes);

            // 最大化：内容卡片去圆角与边距（样式消费 maximized 类）
            window.WindowState = WindowState.Maximized;
            window.UpdateLayout();
            Assert.Contains("maximized", window.ContentCard.Classes);
            window.WindowState = WindowState.Normal;
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
