using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 主窗口 chrome 层补测（2026-09-22 测试审计：标题栏关闭钮、设置类 Enter 保存分发与
/// 持久化窗口状态应用三块行为此前无任何测试命中——OnWindowClosing/HandleSettingsEnterSave
/// 的 ProxyAddressBox 分支/OnDataContextChanged 的 applier 挂钩全部未覆盖）。
/// </summary>
[Collection("sequential")]
public class MainWindowChromeHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public MainWindowChromeHeadlessTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task CaptionClose_RealClick_ClosesWindowAndPersistsDimensions()
    {
        await _ctx.Vm.InitializeAsync();
        var closed = false;

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();
            window.Closed += (_, _) => closed = true;

            // 真实指针走命中链路（直接调 OnCloseClick 的视图层断裂测不出——先例：toast 关闭钮）
            var close = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "CaptionClose");
            var center = close.TranslatePoint(
                new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }, CancellationToken.None);

        // 关闭即持久化：XAML 固定 1464×720 应写回 games.json（模型断言，不碰原始文本）
        Assert.True(closed);
        var settings = _ctx.CatalogService.Catalog!.Settings;
        Assert.Equal(1464, settings.WindowWidth);
        Assert.Equal(720, settings.WindowHeight);
    }

    [Fact]
    public async Task SettingsEnter_OnProxyAddressBox_SavesProxyDraft()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var settings = (SettingsViewModel)_ctx.Vm.CurrentPage!;
            settings.ProxyManual = true;
            settings.ProxyAddressDraft = "http://127.0.0.1:7890";
            var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "ProxyAddressBox");

            // 非 Enter 键必须无动作（早退分支）
            box.RaiseEvent(NewKey(Key.Down));
            Assert.NotEqual(Core.Models.ProxyMode.Manual, _ctx.CatalogService.Catalog!.Settings.ProxyMode);

            // Enter 经类处理器（handledEventsToo）触发代理草稿保存
            box.RaiseEvent(NewKey(Key.Enter));
            Dispatcher.UIThread.RunJobs();

            Assert.False(settings.ProxySave.Failed);
            Assert.NotEmpty(settings.ProxySave.Message);
            Assert.Equal(Core.Models.ProxyMode.Manual, _ctx.CatalogService.Catalog!.Settings.ProxyMode);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PersistedMaximizedState_AppliedWhenCatalogLoads()
    {
        // 目录加载完成后由 VM 经 WindowStateApplier 回放持久化状态：窗口在 DataContext
        // 挂钩时注册 applier，InitializeAsync 末尾触发——顺序错了就永远不应用
        var config = VmFactory.SampleConfigJson.Replace(
            "\"settings\": {",
            "\"settings\": { \"windowWidth\": 900, \"windowHeight\": 600, \"windowMaximized\": true,");
        var ctx = VmFactory.Build(configJson: config);
        using var _ = ctx;
        MainWindow? window = null;

        await HeadlessSession.Instance.Dispatch(() =>
        {
            window = new MainWindow { DataContext = ctx.Vm };
            window.Show();
            window.UpdateLayout();
            // 窗口已挂接（applier 已注册），目录加载必须仍在会话线程内驱动：
            // Games 是 ObservableCollection，跨线程变更会打进已挂接的绑定
            var init = ctx.Vm.InitializeAsync();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!init.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }

            Assert.True(init.IsCompleted, "InitializeAsync 10s 内未完成（RunJobs 泵停摆）");
        }, CancellationToken.None);

        await HeadlessSession.Instance.Dispatch(() =>
        {
            Assert.Equal(WindowState.Maximized, window!.WindowState);
            window!.Close();
        }, CancellationToken.None);
    }

    private static KeyEventArgs NewKey(Key key) => new()
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Route = Avalonia.Interactivity.RoutingStrategies.Bubble,
        Key = key,
    };
}
