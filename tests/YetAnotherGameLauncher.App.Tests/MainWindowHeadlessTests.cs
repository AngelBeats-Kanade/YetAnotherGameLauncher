using Avalonia.Controls;
using Avalonia.VisualTree;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Views;
using Xunit;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>真实窗口的 Headless 集成测试：XAML 构建、绑定与列表渲染。</summary>
[Collection("sequential")]
public class MainWindowHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public MainWindowHeadlessTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task MainWindow_ShowsTwoGamesFromSampleConfig()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();

            var list = window.FindControl<ListBox>("GamesList");
            Assert.NotNull(list);
            Assert.Equal(2, list.ItemCount);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainWindow_GameDetailShowsSelectedGameName()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(tb => tb.Text).ToList();
            Assert.Contains("鸣潮", texts);
            Assert.Contains("尚未安装", texts);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainWindow_SettingsPageSwitchesContent()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout(); // 换页后的模板构建在下轮布局，先同步

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(tb => tb.Text).ToList();
            Assert.Contains("设置", texts);
            Assert.Contains("外观", texts); // 主题选择器移到设置页后的外观卡
            Assert.Contains("主题", texts);
            Assert.Contains("语言", texts);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainWindow_AboutPageSwitchesContent()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            _ctx.Vm.ShowAboutCommand.Execute(null);
            window.UpdateLayout(); // 换页后的模板构建在下轮布局，先同步

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(tb => tb.Text).ToList();
            Assert.Contains("关于", texts);
            Assert.Contains("YetAnotherGameLauncher", texts);
            Assert.Contains("第三方组件", texts);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainWindow_SidebarToggle_CollapsesWidthAndTexts()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();

            var gameName = window.GetVisualDescendants()
                .OfType<TextBlock>().First(tb => tb.Text == "鸣潮");
            var brand = window.GetVisualDescendants()
                .OfType<TextBlock>().First(tb => tb.Text == "YetAnother");

            _ctx.Vm.ToggleSidebarCommand.Execute(null);

            Assert.Equal(68, _ctx.Vm.SidebarWidth);
            Assert.False(brand.IsEffectivelyVisible); // 收起：产品名与游戏名文本不再有效可见
            Assert.False(gameName.IsEffectivelyVisible);

            _ctx.Vm.ToggleSidebarCommand.Execute(null);
            Assert.Equal(264, _ctx.Vm.SidebarWidth);
            Assert.True(brand.IsEffectivelyVisible);
            Assert.True(gameName.IsEffectivelyVisible);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainWindow_LanguageSwitch_UpdatesRenderedTexts()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();

            _ctx.Vm.Loc.SetLanguage("en-US");
            window.UpdateLayout();
            // Item[] 通知后 XAML 绑定刷新：详情页按钮文本变为英文
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(tb => tb.Text).ToList();
            Assert.Contains("Install Game", texts);
            window.Close();
        }, CancellationToken.None);
    }
}
