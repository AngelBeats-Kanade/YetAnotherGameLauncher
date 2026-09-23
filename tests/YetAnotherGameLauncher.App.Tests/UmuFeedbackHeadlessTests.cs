using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 组件检查/Proton 更新反馈的渲染位置回归（2026-09-23）：三个 umu/Proton 操作的反馈曾全部
/// 写进位置卡的 Save 槽——"绑对属性、放错卡"，VM 层断言拦不住。本测试在视觉树层面钉死归属：
/// UmuFeedback 槽的渲染点必须在启动卡（LaunchCard）子树内，Save 槽的渲染点必须在位置卡内，互不越界。
/// umu 面板有 IsNativeUmuMode（Linux）门控，故注入 Linux 平台语义（先例 LaunchErrorOverlayHeadlessTests）。
/// </summary>
[Collection("sequential")]
public class UmuFeedbackHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public UmuFeedbackHeadlessTests() => _ctx = VmFactory.Build(
        platformInfo: new FakePlatformInfo(isLinux: true),
        linuxProtonVersions: []);

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task UmuFeedback_RendersInsideLaunchCard_SaveStaysInLocationCard()
    {
        await _ctx.Vm.InitializeAsync();

        var umuShownInLaunchCard = false;
        var umuHiddenInLocationCard = false;
        var saveShownInLocationCard = false;
        var saveHiddenInLaunchCard = false;

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();
            // 切页后模板在下轮布局构建：RunJobs + 二次 UpdateLayout 冲刷后再查找
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var launch = Assert.IsType<GameSettingsViewModel>(_ctx.Vm.CurrentPage!).LaunchSettings;
            var launchCard = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "LaunchCard");

            // 启动卡槽点亮：渲染点必须在 LaunchCard 子树内，位置卡槽保持隐藏
            launch.UmuFeedback.SetSuccess("组件反馈标记");
            window.UpdateLayout();
            var slots = window.GetVisualDescendants()
                .OfType<TextBlock>().Where(t => t.Classes.Contains("save-msg")).ToList();
            var inLaunchCard = slots.Where(t => launchCard.GetVisualDescendants().Contains(t)).ToList();
            var outsideLaunchCard = slots.Except(inLaunchCard).ToList();
            umuShownInLaunchCard = inLaunchCard.Count == 1
                && inLaunchCard[0].IsVisible
                && inLaunchCard[0].Text == "组件反馈标记";
            umuHiddenInLocationCard = outsideLaunchCard.Count == 1 && !outsideLaunchCard[0].IsVisible;

            // 反向：保存槽点亮时两槽归属互换（Save 消息只出现在位置卡）
            launch.UmuFeedback.Clear();
            launch.Save.SetSuccess("保存反馈标记");
            window.UpdateLayout();
            saveShownInLocationCard = outsideLaunchCard.Count == 1
                && outsideLaunchCard[0].IsVisible
                && outsideLaunchCard[0].Text == "保存反馈标记";
            saveHiddenInLaunchCard = inLaunchCard.Count == 1 && !inLaunchCard[0].IsVisible;

            window.Close();
        }, CancellationToken.None);

        Assert.True(umuShownInLaunchCard, "UmuFeedback 消息未在启动卡（LaunchCard）内显示");
        Assert.True(umuHiddenInLocationCard, "UmuFeedback 消息泄漏到了位置卡的 Save 槽");
        Assert.True(saveShownInLocationCard, "Save 消息未在位置卡内显示");
        Assert.True(saveHiddenInLaunchCard, "Save 消息泄漏到了启动卡");
    }

    /// <summary>
    /// 两个 save-msg 槽都直接显示 ex.Message 等长文案，NoWrap 会横向溢出被卡片裁掉
    /// （AGENTS.md「长文案必须限宽换行」纪律；2026-09-23 review 补钉）。
    /// TextWrapping 是静态样式属性，无需点亮消息即可断言。
    /// </summary>
    [Fact]
    public async Task SaveMsgSlots_WrapLongFeedback_InsteadOfClipping()
    {
        await _ctx.Vm.InitializeAsync();

        var launchCardSlotWraps = false;
        var locationCardSlotWraps = false;

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();
            // 切页后模板在下轮布局构建：RunJobs + 二次 UpdateLayout 冲刷后再查找
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var launchCard = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "LaunchCard");
            var slots = window.GetVisualDescendants()
                .OfType<TextBlock>().Where(t => t.Classes.Contains("save-msg")).ToList();
            var inLaunchCard = slots.Where(t => launchCard.GetVisualDescendants().Contains(t)).ToList();
            var outsideLaunchCard = slots.Except(inLaunchCard).ToList();
            launchCardSlotWraps = inLaunchCard.Count == 1
                && inLaunchCard[0].TextWrapping == TextWrapping.Wrap;
            locationCardSlotWraps = outsideLaunchCard.Count == 1
                && outsideLaunchCard[0].TextWrapping == TextWrapping.Wrap;

            window.Close();
        }, CancellationToken.None);

        Assert.True(launchCardSlotWraps, "启动卡 save-msg 未设 Wrap：长反馈（ex.Message）会横向溢出裁切");
        Assert.True(locationCardSlotWraps, "位置卡 save-msg 未设 Wrap：长反馈会横向溢出裁切");
    }
}
