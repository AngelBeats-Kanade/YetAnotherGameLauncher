using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Views;
using YetAnotherGameLauncher.ViewModels;
using Xunit;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 设置类输入框"按 Enter 保存"的回归：安装根目录（设置页）与游戏安装目录（启动设置页）
/// 曾只有按钮入口，回车保存是选择目录按钮落地后的配套交互。
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
}
