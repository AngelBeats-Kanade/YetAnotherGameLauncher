using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 设置页/游戏设置页的 headless UI 回归集合。
/// 安装根目录与游戏安装目录的"按 Enter 保存"——曾只有按钮入口，回车保存是选择目录按钮落地后的配套交互。
/// 聚焦后走 headless 键盘管线发 Enter：TextBox 会把 Enter 标记为已处理，
/// 窗口层以 handledEventsToo:true 订阅才能收到——正是真机的完整路由路径。
///
/// 断言纪律（2026-09 实测）：本仓库 Avalonia 12.1.2 的 Dispatch(Func&lt;Task&gt;) 实为"发射后不管"——
/// lambda 在首个 await 处即被放弃（返回的任务不等待 lambda 完成），lambda 内抛出的断言异常
/// 一律不传播、被静默吞掉。因此交互（构造窗口/发事件/点击）放在 lambda 内且保持无 await，
/// 断言与异步等待的轮询一律放在 Dispatch 之外。
/// </summary>
[Collection("sequential")]
public class SettingsHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public SettingsHeadlessTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    /// <summary>从磁盘重新解析 games.json：文件级断言不走内存目录，也不做字符串 Contains（转义形态会骗过它）。</summary>
    private GameCatalog DeserializeConfig() =>
        JsonSerializer.Deserialize<GameCatalog>(File.ReadAllText(_ctx.ConfigPath), Json.Default)
        ?? throw new InvalidOperationException("配置文件反序列化结果为空");

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
            // 切页后模板在下轮布局构建：RunJobs + 二次 UpdateLayout 冲刷后再查找
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            // x:Name 在 DataTemplate 命名空间内：窗口 FindControl 找不到，走视觉树按 Name 取
            var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "InstallRootBox");
            box.Text = @"D:\Games\NewRoot";
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            window.UpdateLayout();
            window.Close();
        }, CancellationToken.None);

        // 命令由处理器以 fire-and-forget 启动：轮询等待异步落盘完成（断言在 Dispatch 外才有效）
        var settings = Assert.IsType<SettingsViewModel>(_ctx.Vm.CurrentPage);
        Assert.Equal(@"D:\Games\NewRoot", settings.InstallRootDraft);
        for (var i = 0; i < 100 && !settings.InstallRootSave.HasMessage; i++)
        {
            await Task.Delay(20);
        }
        Assert.True(settings.InstallRootSave.HasMessage);
        Assert.False(settings.InstallRootSave.Failed);
        // 文件级验证必须走 JSON 解析：文件里的反斜杠是转义形态（D:\\Games），Replace('\\','/') 会得到假斜杠串
        var persisted = DeserializeConfig();
        Assert.Equal(@"D:\Games\NewRoot", persisted.Settings.InstallRoot);
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
            // 同上：DataTemplate 命名空间内的 x:Name 需走视觉树查找
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "InstallDirBox");
            box.Text = @"E:\Games\Endfield";
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            window.UpdateLayout();
            window.Close();
        }, CancellationToken.None);

        var launch = Assert.IsType<GameSettingsViewModel>(_ctx.Vm.CurrentPage!).LaunchSettings;
        Assert.Equal(@"E:\Games\Endfield", launch.InstallDirDraft);
        for (var i = 0; i < 100 && !launch.Save.HasMessage; i++)
        {
            await Task.Delay(20);
        }
        Assert.True(launch.Save.HasMessage);
        Assert.False(launch.Save.Failed);
        // 文件级验证走 JSON 解析（原因同上）：游戏安装目录落盘为相对安装根目录或绝对路径
        var persisted = DeserializeConfig();
        Assert.Contains(persisted.Games, g =>
            string.Equals(g.InstallDir.Replace('\\', '/'), "E:/Games/Endfield", StringComparison.OrdinalIgnoreCase));
        // 实际变更落盘后弹轻提示：消息列出变更字段（安装目录）
        for (var i = 0; i < 100 && _ctx.Vm.Toasts.Count == 0; i++)
        {
            await Task.Delay(20);
        }
        var toast = Assert.Single(_ctx.Vm.Toasts);
        Assert.Equal("已更新：安装目录", toast.Message);
    }

    /// <summary>
    /// 「保存启动设置」按钮是启动卡草稿字段（启动方式/命令模板/工作目录/环境变量）唯一可见的保存入口：
    /// 浏览/回车只覆盖位置卡、Proton 下拉只覆盖发行版，按钮被删时这些改动将无从落盘。
    /// 真实指针点击（BringIntoView 后走 Mouse 命中链路）：追加的环境变量写回 games.json 并弹变更轻提示。
    /// </summary>
    [Fact]
    public async Task SaveLaunchOptionsButton_Click_PersistsEnvironmentEdits()
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

            var launch = Assert.IsType<GameSettingsViewModel>(_ctx.Vm.CurrentPage!).LaunchSettings;
            var envBox = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "EnvironmentBox");
            envBox.Text = launch.EnvironmentText + Environment.NewLine + "YAGL_UI_TEST_MARKER=1";
            var saveButton = window.GetVisualDescendants().OfType<Button>()
                .First(b => ReferenceEquals(b.Command, launch.SaveCommand));
            // 启动卡底部可能滚动到窗口可视区外：先 BringIntoView 再走真实指针
            saveButton.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var center = saveButton.TranslatePoint(
                new Point(saveButton.Bounds.Width / 2, saveButton.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            window.Close();
        }, CancellationToken.None);

        var launch = Assert.IsType<GameSettingsViewModel>(_ctx.Vm.CurrentPage!).LaunchSettings;
        for (var i = 0; i < 100 && !launch.Save.HasMessage; i++)
        {
            await Task.Delay(20);
        }
        Assert.True(launch.Save.HasMessage);
        Assert.False(launch.Save.Failed);
        // 文件级验证走 JSON 解析：环境变量落盘为 KEY/VALUE 对象而非 KEY=VALUE 行
        var persisted = DeserializeConfig();
        Assert.Contains(persisted.Games, g =>
            g.Launch.Environment.TryGetValue("YAGL_UI_TEST_MARKER", out var value) && value == "1");
        // 实际变更落盘后弹轻提示：新增变量键按 UI 词汇提示为"环境变量"
        for (var i = 0; i < 100 && _ctx.Vm.Toasts.Count == 0; i++)
        {
            await Task.Delay(20);
        }
        var toast = Assert.Single(_ctx.Vm.Toasts);
        Assert.Equal("已更新：环境变量", toast.Message);
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

        TextWrapping wrapping = default;
        ScrollBarVisibility horizontalScrollBar = default;
        double extentWidth = 0, viewportWidth = 0;

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
            wrapping = box.TextWrapping;
            // 横滚条必须 Disabled（Wrap 的配套效果）：一旦出现即悬浮遮挡最后一行
            horizontalScrollBar = box.GetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty);

            // 行为级验证：超长行换行后无水平溢出（Extent ≤ Viewport），内容不可能被右缘裁切
            box.Text = "WINEPREFIX=/tmp/yagl-tests/11111111-2222-3333-4444-555555555555/data-home/yagl/prefixes/some-game";
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var host = box.GetVisualDescendants().OfType<ScrollViewer>().Single();
            extentWidth = host.Extent.Width;
            viewportWidth = host.Viewport.Width;
            window.Close();
        }, CancellationToken.None);

        Assert.Equal(TextWrapping.Wrap, wrapping);
        Assert.Equal(ScrollBarVisibility.Disabled, horizontalScrollBar);
        Assert.True(extentWidth <= viewportWidth,
            $"水平溢出仍存在：extent={extentWidth}, viewport={viewportWidth}");
    }
}
