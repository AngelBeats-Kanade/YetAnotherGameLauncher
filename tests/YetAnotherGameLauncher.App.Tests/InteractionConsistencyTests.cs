using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 交互一致性（2026-09-23 UI 评审 P2-5 切片 / P2-6）：
/// 1) 收起态侧栏游戏项（纯图标）必须带名称 ToolTip；
/// 2) 设置页安装根目录的"浏览"与输入框同行（与游戏设置页同款 inline 布局）；
/// 3) 启动设置保存钮仅在草稿有未保存变更时可点（脏状态可见，改完点"返回游戏"即丢的缺口收口）。
/// </summary>
[Collection("sequential")]
public class InteractionConsistencyTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public InteractionConsistencyTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task SidebarGameItems_CarryToolTip_WithDisplayName()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        var tooltipFound = false;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var gamesList = window.FindControl<ListBox>("GamesList");
            tooltipFound = gamesList!.GetVisualDescendants()
                .OfType<Control>()
                .Any(c => c.GetValue(ToolTip.TipProperty) is string tip && tip == wuwa.DisplayName);
            window.Close();
        }, CancellationToken.None);

        Assert.True(tooltipFound, $"侧栏游戏项缺少 ToolTip（应为游戏名「{wuwa.DisplayName}」）");
    }

    [Fact]
    public async Task SettingsPage_InstallRootBrowse_SitsOnSameRowAsInput()
    {
        await _ctx.Vm.InitializeAsync();

        var sameRow = false;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm, Width = 1120, Height = 720 };
            window.Show();
            window.UpdateLayout();

            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();

            var settings = Assert.IsType<SettingsViewModel>(_ctx.Vm.CurrentPage);
            // UserControl 内具名元素不在窗口 namescope，走视觉树（docs/UI_STRUCTURE.md §1）
            var box = window.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => t.Name == "InstallRootBox");
            var browse = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => ReferenceEquals(b.Command, settings.BrowseInstallRootCommand));
            if (box is not null && browse is not null)
            {
                var boxY = box.TranslatePoint(new Point(0, 0), window)!.Value.Y;
                var browseY = browse.TranslatePoint(new Point(0, 0), window)!.Value.Y;
                sameRow = Math.Abs(boxY - browseY) < 4; // 同行：垂直位置一致（堆叠布局差一个行高以上）
            }

            window.Close();
        }, CancellationToken.None);

        Assert.True(sameRow, "安装根目录的浏览钮未与输入框同行（仍是堆叠布局）");
    }

    [Fact]
    public async Task LaunchSettings_SaveButton_EnabledOnlyWhileDirty()
    {
        await _ctx.Vm.InitializeAsync();

        var enabledInitially = true;
        var enabledAfterEdit = false;
        var enabledAfterSave = true;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm, Width = 1120, Height = 720 };
            window.Show();
            window.UpdateLayout();

            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();

            var settings = Assert.IsType<GameSettingsViewModel>(_ctx.Vm.CurrentPage);
            var save = window.GetVisualDescendants().OfType<Button>()
                .First(b => ReferenceEquals(b.Command, settings.LaunchSettings.SaveCommand));
            enabledInitially = save.IsEnabled;
            settings.LaunchSettings.WorkingDirectory = "{installDir}/saves";
            window.UpdateLayout();
            enabledAfterEdit = save.IsEnabled;

            PumpToCompletion(() => settings.LaunchSettings.SaveCommand.ExecuteAsync(null));
            window.UpdateLayout();
            enabledAfterSave = save.IsEnabled;
            window.Close();
        }, CancellationToken.None);

        Assert.False(enabledInitially, "无变更时保存钮不应可点");
        Assert.True(enabledAfterEdit, "草稿变更后保存钮应点亮");
        Assert.False(enabledAfterSave, "保存成功后保存钮应熄灭");
    }

    [Fact]
    public async Task LaunchSettings_IsDirty_TracksDrafts_AndResetsOnSave()
    {
        await _ctx.Vm.InitializeAsync();
        _ctx.Vm.ShowGameSettingsCommand.Execute(null);
        var settings = Assert.IsType<GameSettingsViewModel>(_ctx.Vm.CurrentPage).LaunchSettings;

        Assert.False(settings.IsDirty);
        settings.CommandTemplate = $"{settings.CommandTemplate} --flag".Trim();
        Assert.True(settings.IsDirty, "命令模板变更后 IsDirty 应为 true");
        settings.CommandTemplate = "{exe}";
        Assert.False(settings.IsDirty, "改回原值后 IsDirty 应复位");

        settings.EnvironmentText = "KEY=value";
        Assert.True(settings.IsDirty);
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.False(settings.IsDirty, "保存成功后 IsDirty 应复位");
    }

    /// <summary>在会话线程上泵到异步步骤完成（UiScreenshotTests.RunToCompletion 同款）。</summary>
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
}
