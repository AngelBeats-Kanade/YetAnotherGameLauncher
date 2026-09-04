using Avalonia.Controls;
using Avalonia.VisualTree;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Views;
using Xunit;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>真实窗口的 Headless 集成测试：XAML 构建、绑定与列表渲染。</summary>
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

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(tb => tb.Text).ToList();
            Assert.Contains("设置", texts);
            window.Close();
        }, CancellationToken.None);
    }
}
