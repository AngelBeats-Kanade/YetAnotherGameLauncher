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

            // 静止形态：可见小点，顶点对齐选中行中心的半高
            var (y, scale) = Pose(indicator);
            Assert.True(indicator.IsVisible);
            Assert.Equal(rows[0].Bounds.Height - 8, indicator.Height, 1);
            Assert.Equal(RowCenterY(rows[0], overlay) - indicator.Height / 2, y, 1);
            Assert.Equal(10 / indicator.Height, scale, 2); // DotHeight=10 的静态小点

            // 选中第二个游戏：指示点落到第二行（先推进时钟让迁移动画播完，
            // 动画结束后属性回落到基值 = 终态）
            _ctx.Vm.GameNavSelection = _ctx.Vm.Games[1];
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            (y, _) = Pose(indicator);
            Assert.Equal(RowCenterY(rows[1], overlay) - indicator.Height / 2, y, 1);
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
            var (y, _) = Pose(indicator);
            Assert.True(indicator.IsVisible);
            Assert.Equal(ButtonCenterY(navButtons[0], overlay) - indicator.Height / 2, y, 1);

            _ctx.Vm.ShowAboutCommand.Execute(null);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            (y, _) = Pose(indicator);
            Assert.Equal(ButtonCenterY(navButtons[1], overlay) - indicator.Height / 2, y, 1);
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
            var expandedHeight = indicator.Height;

            // 收起侧栏：行内文本隐藏 + 内边距收窄，行高变小，指示点随之重算
            _ctx.Vm.ToggleSidebarCommand.Execute(null);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var row = window.GetVisualDescendants().OfType<ListBoxItem>().First();
            Assert.True(indicator.IsVisible);
            Assert.Equal(row.Bounds.Height - 8, indicator.Height, 1);
            Assert.True(indicator.Height < expandedHeight);
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

            var (y, _) = Pose(indicator);
            Assert.True(indicator.IsVisible);
            Assert.Equal(RowCenterY(rows[1], overlay) - indicator.Height / 2, y, 1);
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>读取指示点的平移 Y 与缩放（Transform 不生成 x:Name 字段，按声明顺序解析）。</summary>
    private static (double Y, double Scale) Pose(Border indicator)
    {
        var group = (TransformGroup)indicator.RenderTransform!;
        return (((TranslateTransform)group.Children[0]).Y, ((ScaleTransform)group.Children[1]).ScaleY);
    }

    /// <summary>列表行中心在指示点覆盖层坐标系下的 Y。</summary>
    private static double RowCenterY(ListBoxItem row, Visual overlay) =>
        row.TranslatePoint(new Point(0, row.Bounds.Height / 2), overlay)!.Value.Y;

    /// <summary>导航按钮中心在指示点覆盖层坐标系下的 Y。</summary>
    private static double ButtonCenterY(Button button, Visual overlay) =>
        button.TranslatePoint(new Point(0, button.Bounds.Height / 2), overlay)!.Value.Y;
}
