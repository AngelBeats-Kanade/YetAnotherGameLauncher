using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Views;
using YetAnotherGameLauncher.ViewModels;
using Xunit;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 设置页/游戏设置页的 headless UI 回归集合。
/// 安装根目录与游戏安装目录的"按 Enter 保存"——曾只有按钮入口，回车保存是选择目录按钮落地后的配套交互。
/// 聚焦后走 headless 键盘管线发 Enter：TextBox 会把 Enter 标记为已处理，
/// 窗口层以 handledEventsToo:true 订阅才能收到——正是真机的完整路由路径。
/// </summary>
[Collection("sequential")]
public class SettingsHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public SettingsHeadlessTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task InstallRootBox_Enter_SavesConfig()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(async () =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();

            var settings = Assert.IsType<SettingsViewModel>(_ctx.Vm.CurrentPage);
            // 切页后模板在下轮布局构建：RunJobs + 二次 UpdateLayout 冲刷后再查找
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            // x:Name 在 DataTemplate 命名空间内：窗口 FindControl 找不到，走视觉树按 Name 取
            var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "InstallRootBox");
            box.Text = @"D:\Games\NewRoot";
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            window.UpdateLayout();
            Assert.Equal(@"D:\Games\NewRoot", settings.InstallRootDraft);
            // 命令由处理器以 fire-and-forget 启动：轮询等待异步落盘完成
            for (var i = 0; i < 100 && !settings.InstallRootSave.HasMessage; i++)
            {
                await Task.Delay(20);
            }
            Assert.True(settings.InstallRootSave.HasMessage);
            Assert.False(settings.InstallRootSave.Failed);
            Assert.Contains("D:/Games/NewRoot", File.ReadAllText(_ctx.ConfigPath).Replace('\\', '/'));
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task InstallDirBox_Enter_SavesLaunchSettings()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(async () =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();

            var launch = Assert.IsType<GameSettingsViewModel>(_ctx.Vm.CurrentPage!).LaunchSettings;
            // 同上：DataTemplate 命名空间内的 x:Name 需走视觉树查找
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "InstallDirBox");
            box.Text = @"E:\Games\Endfield";
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            window.UpdateLayout();
            Assert.Equal(@"E:\Games\Endfield", launch.InstallDirDraft);
            for (var i = 0; i < 100 && !launch.Save.HasMessage; i++)
            {
                await Task.Delay(20);
            }
            Assert.True(launch.Save.HasMessage);
            Assert.False(launch.Save.Failed);
            Assert.Contains("E:/Games/Endfield", File.ReadAllText(_ctx.ConfigPath).Replace('\\', '/'));
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>
    /// 环境变量框必须换行：NoWrap + AcceptsReturn 会把内部横滚条可见性算成 Auto，
    /// Fluent 默认悬浮滚动条绘制在内容之上——变量行填满后最后一行被横滚条盖住、指针被拦截无法点选。
    /// Wrap 时 TextBox 把横滚条设为 Disabled，umu 生成的长路径行（WINEPREFIX 等）换行后全部可见。
    /// </summary>
    [Fact]
    public async Task EnvironmentBox_WrapsText_SoLastLineNeverCoveredByHorizontalScrollbar()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(async () =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();
            // 同上：DataTemplate 命名空间内的 x:Name 需走视觉树查找
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "EnvironmentBox");
            Assert.Equal(TextWrapping.Wrap, box.TextWrapping);
            // 横滚条必须 Disabled（Wrap 的配套效果）：一旦出现即悬浮遮挡最后一行
            Assert.Equal(ScrollBarVisibility.Disabled,
                box.GetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty));

            // 行为级验证：超长行换行后无水平溢出（Extent ≤ Viewport），内容不可能被右缘裁切
            box.Text = "WINEPREFIX=/tmp/yagl-tests/11111111-2222-3333-4444-555555555555/data-home/yagl/prefixes/some-game";
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var host = box.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Assert.True(host.Extent.Width <= host.Viewport.Width,
                $"水平溢出仍存在：extent={host.Extent.Width}, viewport={host.Viewport.Width}");
            window.Close();
        }, CancellationToken.None);
    }
}
