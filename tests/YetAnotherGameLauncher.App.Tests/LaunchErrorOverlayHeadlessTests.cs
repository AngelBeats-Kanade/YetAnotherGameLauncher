using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 启动失败覆盖层关闭按钮必须经真实指针命中链路可点（审计 Phase 2 补充，2026-09-19）：
/// 原 LaunchErrorOverlayTests 只测 VM 层 DismissCommand——覆盖层控件的按钮绑定/命中
/// 若断裂（Toast IsHitTestVisible 剪枝的同族风险）该测试拦不住。本测试与 ToastHeadlessTests
/// 同款：MouseMove/MouseDown/MouseUp 走真实命中链路。
/// </summary>
[Collection("sequential")]
public class LaunchErrorOverlayHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public LaunchErrorOverlayHeadlessTests() => _ctx = VmFactory.Build(
        configJson: null,
        templateFactory: () => VmFactory.SampleConfigJson,
        platformInfo: new FakePlatformInfo(isLinux: true),
        linuxProtonVersions: []);

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ClickingOverlayDismissButton_RemovesOverlay_ViaRealHitTesting()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        // 主程序就位 + wine 模板（PATH 已禁用 → wine 缺失）→ 启动预检失败 → 覆盖层
        var exePath = Path.Combine(
            wuwa.InstallDirPath, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllText(exePath, "MZ");
        wuwa.Game.Launch.CommandTemplate = "wine \"{exe}\"";
        await wuwa.RefreshAsync();

        var overlayDismissed = false;
        var overlayShownBeforeClick = false;

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // 异步步骤用 RunJobs 泵到完成（Dispatch 的 async lambda 形态吞断言，禁用）
            var launchTask = wuwa.LaunchCommand.ExecuteAsync(null);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!launchTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }

            Assert.True(launchTask.IsCompleted, "LaunchAsync 10s 内未完成");
            overlayShownBeforeClick = wuwa.HasLaunchError;
            window.UpdateLayout();

            // 覆盖层关闭钮（launch-error-primary 类，绑定 DismissCommand）：走真实指针命中
            var dismiss = window.GetVisualDescendants()
                .OfType<Button>()
                .First(b => b.Classes.Contains("launch-error-primary")
                            && ReferenceEquals(b.Command, wuwa.LaunchError!.DismissCommand));
            var center = dismiss.TranslatePoint(
                new Point(dismiss.Bounds.Width / 2, dismiss.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            window.UpdateLayout();

            overlayDismissed = !wuwa.HasLaunchError;
            window.Close();
        }, CancellationToken.None);

        // 断言在 Dispatch 外
        Assert.True(overlayShownBeforeClick, "前置失败：启动预检未点亮覆盖层");
        Assert.True(overlayDismissed, "真实指针点击覆盖层关闭钮后覆盖层未消失（命中链路或绑定断裂）");
    }
}
