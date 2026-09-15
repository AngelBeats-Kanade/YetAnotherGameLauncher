using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Views;

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
    public async Task ContentCard_UniformPageSheet_BelowTitleBand()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var card = window.FindControl<Border>("ContentCard")!;
            var drag = window.FindControl<Border>("TitleDrag")!;
            var chrome = window.FindControl<Border>("TitleChrome")!;

            // 页面板（所有页面统一，同详情页海报）：色带下方开始、左上角圆角、贴边全出血；
            // 色带 46 高；拖拽条覆盖色带整条
            Assert.Equal(new Thickness(0, 46, 0, 0), card.Margin);
            Assert.Equal(new CornerRadius(10, 0, 0, 0), card.CornerRadius);
            Assert.Equal(56, chrome.Height);
            Assert.Equal(46, drag.Height);

            // 游戏设置页 / 应用设置页：同一页面板布局，容器透明露出页面板内的应用背景
            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal(new Thickness(0, 46, 0, 0), card.Margin);

            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            Assert.Null(card.Background);
            var page = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("page"));
            Assert.Equal(new Thickness(34, 48, 34, 24), page.Margin);

            // 返回详情页：同板
            _ctx.Vm.ShowGamesCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal(new Thickness(0, 46, 0, 0), card.Margin);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GameDetailPage_PosterImage_IsLeftAnchored()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            // 海报靠左上锚定：UniformToFill 的居中裁切曾吃掉海报左缘（被感知为"被侧边栏遮住"）
            var poster = window.GetVisualDescendants().OfType<Image>().First(i => i.Name == "PosterImage");
            Assert.Equal(HorizontalAlignment.Left, poster.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Top, poster.VerticalAlignment);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GameDetailPage_TitleBlockAndChips_ProposalALayout()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var page = window.GetVisualDescendants().OfType<Panel>().First(p => p.Classes.Contains("page"));

            // 标题上墙（方案 A）：30px 游戏名 + 元信息行，位于详情页顶部左侧
            var title = page.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.FontSize == 30 && t.Text == _ctx.Vm.Games[0].DisplayName);
            var titleOrigin = title.TranslatePoint(new Point(0, 0), page)!.Value;
            Assert.True(titleOrigin.X < 60 && titleOrigin.Y < 120,
                $"标题应落在详情页左上角，实际 {titleOrigin}");
            var meta = page.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == _ctx.Vm.Games[0].DetailMetaText);
            Assert.Equal(13, meta.FontSize);

            // 顶部 chips：状态胶囊与版本号分段胶囊（onart-chip 族），不再有页中上方居中的旧合并胶囊
            var chips = page.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("onart-chip")).ToList();
            var statusChip = chips.Single(c => c.Child?.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == _ctx.Vm.Games[0].StatusText) == true);
            var texts = page.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Contains(texts, t => t.Text == _ctx.Vm.Games[0].VersionChipLead);
            Assert.Contains(texts, t => t.Text == _ctx.Vm.Games[0].VersionChipNumber);
            // 顶部纱带：全出血渐变底，不拦交互
            var scrim = page.GetVisualDescendants().OfType<Border>()
                .First(b => ReferenceEquals(b.Background, window.FindResource("AppOnArtworkScrimBrush")));
            Assert.False(scrim.IsHitTestVisible);
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
