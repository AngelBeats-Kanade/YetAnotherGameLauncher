using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 启动选项开关区（2026-09-28）：三个 ToggleSwitch 仅 Linux 显示、按 Content 文案绑定
/// VM 开关草稿、翻转点亮保存钮（草稿语义，与命令模板/环境变量同批落盘）；
/// 自定义启动选项框默认留空（托管键不进编辑框）。断言全部在 Dispatch 之外（Dispatch 三规则②）。
/// </summary>
[Collection("sequential")]
public class LaunchOptionsHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public LaunchOptionsHeadlessTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task ResourceQualityCombo_VisibleForKuro_HiddenForOtherChannels()
    {
        // 鸣潮资源包档位（2026-10-02）：-krqlv 下拉仅鸣潮渠道显示（Game.IsKuro 门控，
        // HasGachaEntry 先例）；终末地设置页不出现。断言在 Dispatch 之外（Dispatch 三规则②）。
        await _ctx.Vm.InitializeAsync();

        var kuroVisible = false;
        var endfieldVisible = true;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm, Width = 1120, Height = 720 };
            window.Show();
            window.UpdateLayout();

            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();
            kuroVisible = window.GetVisualDescendants()
                .OfType<ComboBox>()
                .Any(c => c.IsEffectivelyVisible
                    && c.ItemsSource?.Cast<object>().FirstOrDefault() is ResourceQualityOption);

            // 切到终末地（Games[1]，hypergryph 渠道）再进其设置页
            _ctx.Vm.ShowGamesCommand.Execute(null);
            window.UpdateLayout();
            _ctx.Vm.SelectedGame = _ctx.Vm.Games[1];
            window.UpdateLayout();
            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();
            endfieldVisible = window.GetVisualDescendants()
                .OfType<ComboBox>()
                .Any(c => c.IsEffectivelyVisible
                    && c.ItemsSource?.Cast<object>().FirstOrDefault() is ResourceQualityOption);
            window.Close();
        }, CancellationToken.None);

        Assert.True(kuroVisible, "鸣潮设置页应显示资源包档位下拉");
        Assert.False(endfieldVisible, "终末地设置页不应显示资源包档位下拉");
    }

    [Fact]
    public async Task WindowsPlatform_TogglesHidden()
    {
        await _ctx.Vm.InitializeAsync();

        var toggleCount = -1;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm, Width = 1120, Height = 720 };
            window.Show();
            window.UpdateLayout();

            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();

            // 缺省 Windows 假平台：开关区整块隐藏。IsVisible=false 的元素仍留在视觉树
            // （只是不渲染/不命中），按"有效可见"过滤而不是按存在性
            toggleCount = window.GetVisualDescendants()
                .OfType<ToggleSwitch>()
                .Count(t => t.IsEffectivelyVisible);
            window.Close();
        }, CancellationToken.None);

        Assert.Equal(0, toggleCount);
    }

    [Fact]
    public async Task LinuxPlatform_TogglesBoundToDrafts_AndEnvBoxEmpty()
    {
        using var ctx = VmFactory.Build(platformInfo: new FakePlatformInfo(isLinux: true));
        await ctx.Vm.InitializeAsync();

        var waylandToggleFound = false;
        var envBoxEmpty = false;
        var flipped = false;
        var dirtyAfterFlip = false;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = ctx.Vm, Width = 1120, Height = 720 };
            window.Show();
            window.UpdateLayout();

            ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();

            var settings = Assert.IsType<GameSettingsViewModel>(ctx.Vm.CurrentPage).LaunchSettings;
            var waylandLabel = ctx.Vm.Loc["launch_useWayland"];
            var toggle = window.GetVisualDescendants()
                .OfType<ToggleSwitch>()
                .Where(t => t.IsEffectivelyVisible)
                .FirstOrDefault(t => t.Content as string == waylandLabel);
            waylandToggleFound = toggle is not null;
            envBoxEmpty = settings.EnvironmentText.Length == 0;

            if (toggle is not null)
            {
                // 先落盘一次让初始态干净（首运推荐链写草稿即算脏），再验证翻转点亮
                PumpToCompletion(() => settings.SaveCommand.ExecuteAsync(null));
                var dirtyBeforeFlip = settings.IsDirty;

                toggle.IsChecked = true;
                window.UpdateLayout();
                flipped = settings.UseWaylandDraft;
                dirtyAfterFlip = settings.IsDirty && !dirtyBeforeFlip;
            }

            window.Close();
        }, CancellationToken.None);

        Assert.True(waylandToggleFound, "Linux 平台应能看到「使用 Wayland」开关");
        Assert.True(envBoxEmpty, "托管语义：Linux 首运推荐键不进自定义启动选项框，默认留空");
        Assert.True(flipped, "开关翻转应写进 UseWaylandDraft 草稿（绑定断线即假）");
        Assert.True(dirtyAfterFlip, "翻转前 IsDirty 应已复位、翻转后点亮（草稿语义接线）");
    }

    /// <summary>在会话线程上泵到异步步骤完成（InteractionConsistencyTests.PumpToCompletion 同款）。</summary>
    private static void PumpToCompletion(Func<Task> call)
    {
        var task = call();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        Assert.True(task.IsCompleted, "异步步骤 30s 内未完成（RunJobs 泵停摆）");
    }

    [Fact]
    public async Task EnvironmentHelpIcon_PresentWithLocalizedTooltip()
    {
        // 自定义启动选项标签旁的「ⓘ」帮助图标（2026-10-03）：图标可见且 ToolTip 绑定填写规则文案。
        // ToolTip.Tip 绑定在可视树节点上，无需模拟悬停即可断言内容（Dispatch 三规则②）。
        await _ctx.Vm.InitializeAsync();

        var iconFound = false;
        string? tipText = null;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm, Width = 1120, Height = 720 };
            window.Show();
            window.UpdateLayout();

            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();

            var icon = window.GetVisualDescendants()
                .OfType<Avalonia.Controls.Shapes.Path>()
                .FirstOrDefault(p => p.Name == "EnvironmentHelpIcon");
            iconFound = icon is not null && icon.IsEffectivelyVisible;
            if (icon is not null)
            {
                tipText = ToolTip.GetTip(icon) as string;
            }

            window.Close();
        }, CancellationToken.None);

        Assert.True(iconFound, "自定义启动选项标签旁应显示帮助图标");
        Assert.Equal(_ctx.Vm.Loc["launch_environmentTooltip"], tipText);
    }
}
