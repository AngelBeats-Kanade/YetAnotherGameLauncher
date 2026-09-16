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
/// 无头验证 toast 关闭钮必须经真实指针命中测试可点：ToastHost 曾在宿主 ItemsControl 上设
/// IsHitTestVisible=False——Avalonia 会把该视觉连同整棵子树从命中测试中剪掉（与 WPF 不同，
/// 子级设回 True 也翻不回来），关闭钮从此收不到任何指针事件；直接 Execute DismissCommand 的
/// VM 层测试（ToastTests.DismissCommand_RemovesToast）覆盖不到这类视图层断裂，故用
/// MouseDown/MouseUp 走真实命中链路回归。
/// </summary>
[Collection("sequential")]
public class ToastHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public ToastHeadlessTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ClickingCloseButton_RemovesToast_ViaRealHitTesting()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            _ctx.Vm.ShowToast("鸣潮", "可预下载新版本", ToastKind.Warning);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var closeButton = window.GetVisualDescendants()
                .OfType<Button>().First(b => b.Classes.Contains("toast-close"));
            var center = closeButton.TranslatePoint(
                new Point(closeButton.Bounds.Width / 2, closeButton.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            window.UpdateLayout();

            Assert.Empty(_ctx.Vm.Toasts);
            window.Close();
        }, CancellationToken.None);
    }
}
