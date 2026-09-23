using Avalonia.Controls;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 破坏性确认操作不得与建设性主操作共用样式（2026-09-23 UI 评审 P1-3/P1-4）：
/// 1) "确认重装"与 Proton"更新"（均会覆盖/删除旧数据）走 danger 描边样式而非 accent 实心；
/// 2) 启动失败覆盖层在 CanRetry 时"重试"应为主操作、"关闭"退居次级（CanRetry=false 反转，
///    放弃动作不得强于恢复动作）。
/// 类指派断言在视觉树上做（IsVisible=false 的确认层控件仍在树内，无需驱动确认流程）。
/// </summary>
[Collection("sequential")]
public class DangerActionStyleTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public DangerActionStyleTests()
    {
        // Linux + 空 Proton 目录：与 10 号截图同款环境（umu 模板 + umu 未装 → CanRetry=true）
        _ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: []);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task RepairConfirmButton_UsesDangerStyle_NotAccent()
    {
        await _ctx.Vm.InitializeAsync();

        var dangerWired = false;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var confirm = window.GetVisualDescendants().OfType<Button>()
                .First(b => ReferenceEquals(b.Command, _ctx.Vm.Games[0].ConfirmRepairCommand));
            dangerWired = confirm.Classes.Contains("danger-onart")
                          && !confirm.Classes.Contains("accent-onart");
            window.Close();
        }, CancellationToken.None);

        Assert.True(dangerWired, "确认重装按钮未挂 danger-onart（仍走 accent-onart 实心主钮）");
    }

    [Fact]
    public async Task ProtonUpdateConfirmButton_UsesDangerStyle_NotPrimary()
    {
        await _ctx.Vm.InitializeAsync();

        var dangerWired = false;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm, Width = 1120, Height = 720 };
            window.Show();
            window.UpdateLayout();

            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();

            var settings = Assert.IsType<GameSettingsViewModel>(_ctx.Vm.CurrentPage);
            var confirm = window.GetVisualDescendants().OfType<Button>()
                .First(b => ReferenceEquals(b.Command, settings.LaunchSettings.ConfirmProtonUpdateCommand));
            dangerWired = confirm.Classes.Contains("danger-card")
                          && !confirm.Classes.Contains("launch-error-primary");
            window.Close();
        }, CancellationToken.None);

        Assert.True(dangerWired, "Proton 更新确认按钮未挂 danger-card（仍走 launch-error-primary 实心主钮）");
    }

    [Fact]
    public async Task ErrorOverlay_WhenCanRetry_RetryIsPrimary_CloseIsSecondary()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        var retryPrimary = false;
        var closeSecondary = false;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm, Width = 1120, Height = 720 };
            window.Show();
            window.UpdateLayout();

            // P1-4 是视图层关注点：直接给覆盖层喂 CanRetry=true 的错误 VM（umu 组件缺失类），
            // 不经启动流夹具；CanRetry=true 的真实来源（UmuRuntimeMissing）已由 CreateLaunchError 钉住
            wuwa.LaunchError = new LaunchErrorViewModel(
                "Steam Runtime（steamrt）尚未安装。", "detail", null,
                platform: new FakePlatformInfo(isLinux: true), canRetry: true);
            window.UpdateLayout();

            var retry = window.GetVisualDescendants().OfType<Button>()
                .First(b => ReferenceEquals(b.Command, wuwa.LaunchError!.RetryCommand));
            var close = window.GetVisualDescendants().OfType<Button>()
                .First(b => ReferenceEquals(b.Command, wuwa.LaunchError!.DismissCommand));
            retryPrimary = retry.Classes.Contains("launch-error-primary")
                           && !retry.Classes.Contains("launch-error-secondary");
            closeSecondary = close.Classes.Contains("launch-error-secondary")
                             && !close.Classes.Contains("launch-error-primary");
            window.Close();
        }, CancellationToken.None);

        Assert.True(retryPrimary, "CanRetry 时「重试」应为 launch-error-primary 主操作");
        Assert.True(closeSecondary, "CanRetry 时「关闭」应为 launch-error-secondary 次操作");
    }

    [Fact]
    public async Task ErrorOverlay_WhenNoRetry_CloseStaysPrimary()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        var closePrimary = false;
        var retryHidden = false;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm, Width = 1120, Height = 720 };
            window.Show();
            window.UpdateLayout();

            wuwa.LaunchError = new LaunchErrorViewModel(
                "找不到 wine。", "detail", null,
                platform: new FakePlatformInfo(isLinux: true), canRetry: false);
            window.UpdateLayout();

            var close = window.GetVisualDescendants().OfType<Button>()
                .First(b => ReferenceEquals(b.Command, wuwa.LaunchError!.DismissCommand));
            var retry = window.GetVisualDescendants().OfType<Button>()
                .First(b => ReferenceEquals(b.Command, wuwa.LaunchError!.RetryCommand));
            closePrimary = close.Classes.Contains("launch-error-primary");
            retryHidden = !retry.IsVisible;
            window.Close();
        }, CancellationToken.None);

        Assert.True(closePrimary, "不可重试时「关闭」应保持 launch-error-primary 唯一主操作");
        Assert.True(retryHidden, "不可重试时「重试」应保持隐藏");
    }
}
