using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Views;
using Xunit;
using System.Windows.Input;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 游戏详情页相关无头回归：
/// 1) 玻璃按钮（glass-onart）浅色前景必须在禁用/悬停态保持——Fluent 的状态样式把主题前景
///    （亮色主题为黑/灰）直接写在模板 presenter 层，会压过 Button 本体上的任何前景值，
///    因此 App 的浅色前景必须同样下沉到 presenter 层（曾经正是禁用启动按钮在亮色主题黑字）；
/// 2) 详情页全出血布局类（detail）随页面类型切换，其余页面保持浮卡。
/// </summary>
[Collection("sequential")]
public class DetailPageHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public DetailPageHeadlessTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task GlassOnArtButton_KeepsLightForeground_InDisabledAndHoverStates()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.RequestedThemeVariant = ThemeVariant.Light; // 黑字问题只在亮色主题暴露
            window.Show();
            window.UpdateLayout();

            // 真实场景：夹具游戏未安装 → 启动按钮 glass-onart + 禁用（用户报告的黑字状态）
            var launch = FindGlassOnArtButton(window, _ctx.Vm.Games[0].LaunchCommand);
            var launchPresenter = TemplatePresenter(launch);
            Assert.False(launch.IsEnabled);
            Assert.Equal(ArtworkColor(window, "AppOnArtworkSecondary"), PresenterColor(launchPresenter));

            // 探针按钮无绑定干扰：正常态浅色字 → 禁用态切次级浅色 → 恢复 → 悬停仍浅色
            var probe = new Button { Classes = { "glass-onart" }, Width = 80, Height = 36 };
            ((Panel)window.Content!).Children.Add(probe);
            window.UpdateLayout();
            var probePresenter = TemplatePresenter(probe);
            Assert.Equal(ArtworkColor(window, "AppOnArtworkBrush"), PresenterColor(probePresenter));

            probe.IsEnabled = false;
            window.UpdateLayout();
            Assert.Equal(ArtworkColor(window, "AppOnArtworkSecondary"), PresenterColor(probePresenter));

            probe.IsEnabled = true;
            window.UpdateLayout();
            Assert.Equal(ArtworkColor(window, "AppOnArtworkBrush"), PresenterColor(probePresenter));

            var center = probe.TranslatePoint(new Point(probe.Bounds.Width / 2, probe.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            window.UpdateLayout();
            Assert.True(probe.IsPointerOver);
            Assert.Equal(ArtworkColor(window, "AppOnArtworkBrush"), PresenterColor(probePresenter));
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GameDetailPage_TogglesFullBleedDetailClasses()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var card = window.FindControl<Border>("ContentCard")!;
            var drag = window.FindControl<Border>("TitleDrag")!;

            // 详情页：全出血（顶 46 低于标题按钮行，右/下/左贴边），拖拽条拉高覆盖整条暴露区
            Assert.True(_ctx.Vm.IsGameDetailPage);
            Assert.Contains("detail", card.Classes);
            Assert.Contains("detail", drag.Classes);
            Assert.Equal(new Thickness(0, 46, 0, 0), card.Margin);
            Assert.Equal(46, drag.Height);

            // 游戏设置页：属于游戏导航（高亮不丢）但不是详情页 → 浮卡不变
            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();
            Assert.True(_ctx.Vm.IsGameNavActive);
            Assert.False(_ctx.Vm.IsGameDetailPage);
            Assert.DoesNotContain("detail", card.Classes);
            Assert.Equal(new Thickness(0, 6, 6, 6), card.Margin);
            Assert.Equal(40, drag.Height);

            // 应用设置页浮卡；返回详情页恢复全出血
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            Assert.DoesNotContain("detail", card.Classes);

            _ctx.Vm.ShowGamesCommand.Execute(null);
            window.UpdateLayout();
            Assert.Contains("detail", card.Classes);
            Assert.Equal(new Thickness(0, 46, 0, 0), card.Margin);
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>按命令找到详情页操作行里的玻璃按钮（预下载/应用预下载/启动都可能带 glass-onart）。</summary>
    private static Button FindGlassOnArtButton(MainWindow window, ICommand command) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("glass-onart") && ReferenceEquals(b.Command, command));

    /// <summary>取按钮模板里的内容呈现器（Fluent 与 App 状态样式的共同作用点）。</summary>
    private static ContentPresenter TemplatePresenter(Button button) =>
        button.GetVisualDescendants().OfType<ContentPresenter>()
            .First(p => p.Name == "PART_ContentPresenter");

    private static Color PresenterColor(ContentPresenter presenter) =>
        Assert.IsType<SolidColorBrush>(presenter.Foreground).Color;

    private static Color ArtworkColor(MainWindow window, string key) =>
        Assert.IsType<SolidColorBrush>(window.FindResource(key)).Color;
}
