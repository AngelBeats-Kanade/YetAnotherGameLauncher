using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 关闭按钮驻留行为（settings.closeAction=HideToTray）的窗口层回归：
/// 点关闭钮应隐藏窗口而非销毁（应用驻留托盘），且尺寸照常持久化；
/// 托盘"显示主窗口"可唤回，托盘"退出"经真关闭放行；
/// 默认 Exit 语义不变（由 MainWindowChromeHeadlessTests 既有用例守卫）。
/// </summary>
[Collection("sequential")]
public class CloseToTrayHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public CloseToTrayHeadlessTests() => _ctx = VmFactory.Build(configJson: VmFactory.SampleConfigJson.Replace(
        "\"settings\": {",
        "\"settings\": { \"closeAction\": \"HideToTray\","));

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task CaptionClose_HideToTrayConfig_HidesWindowInsteadOfClosing()
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
            CloseViaCaptionButton(window);

            // 驻留模式：窗口应被隐藏（IsVisible=false）而非走上销毁路径——断言必须先于清理关闭
            Assert.False(window.IsVisible);
            Assert.False(closed);

            // 清理：摘掉 DataContext 绕过驻留拦截（拦截仅对挂接 VM 的关闭生效；
            // 此关闭会触发 Closed，"未销毁"结论已由 Dispatch 内先于清理的断言钉住）
            window.DataContext = null;
            window.Close();
        }, CancellationToken.None);

        // 隐藏路径同样持久化窗口尺寸（XAML 固定 1464×720）
        var settings = _ctx.CatalogService.Catalog!.Settings;
        Assert.Equal(1464, settings.WindowWidth);
        Assert.Equal(720, settings.WindowHeight);
    }

    [Fact]
    public async Task ShowFromTray_AfterCloseToTrayHide_ShowsWindowAgain()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            CloseViaCaptionButton(window);
            Assert.False(window.IsVisible); // 已隐藏驻留

            window.ShowFromTray(); // 托盘"显示主窗口"
            Assert.True(window.IsVisible);

            window.DataContext = null;
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RequestRealClose_InHideMode_ClosesForRealAndPersists()
    {
        await _ctx.Vm.InitializeAsync();
        var closed = false;

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();
            window.Closed += (_, _) => closed = true;

            CloseViaCaptionButton(window);
            Assert.False(window.IsVisible); // 驻留模式下普通关闭被拦截

            window.RequestRealClose(); // 托盘"退出"：放行真关闭（含停视频/取消依赖安装的清理路径）
            Assert.True(closed);
        }, CancellationToken.None);

        Assert.True(closed);
        // 真关闭路径同样持久化窗口尺寸
        var settings = _ctx.CatalogService.Catalog!.Settings;
        Assert.Equal(1464, settings.WindowWidth);
        Assert.Equal(720, settings.WindowHeight);
    }

    /// <summary>经真实指针点击标题栏关闭钮（命中链路断裂测不出——先例：toast 关闭钮）。</summary>
    private static void CloseViaCaptionButton(MainWindow window)
    {
        var close = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "CaptionClose");
        var center = close.TranslatePoint(
            new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
        window.MouseMove(center);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
