using Xunit;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 打开目录类按钮的平台失败防线（F17，artifacts/bugs.md）：xdg-open 缺失时 Process.Start 抛
/// Win32Exception（官方文档："The file specified in the fileName could not be found"），
/// 两个裸调用点曾让一次按钮点击崩掉整个进程（全局 UnhandledException 处理器不设 e.Handled）。
/// 对照防线：OpenProjectHome 的 catch + toast。
/// sequential 集合：VmFactory 触发 headless 平台初始化，与并行组的首初始化竞争即 Compositor
/// 处炸 InvalidOperationException（AGENTS.md 同族实锤；Settings 系 VmFactory 测试全部在此集合）。
/// </summary>
[Collection("sequential")]
public class OpenFolderFailureTests
{
    [Fact]
    public void LaunchErrorOverlay_OpenLogDirectory_WhenPlatformThrows_DoesNotEscape()
    {
        var platform = new FakePlatformInfo(isLinux: true) { ThrowOnOpenDirectory = true };
        var vm = new LaunchErrorViewModel(
            "message", logPath: "/tmp/logs/launch-20260925.log", platform: platform);

        var ex = Record.Exception(() => vm.OpenLogDirectoryCommand.Execute(null));

        Assert.Null(ex); // 红落此断言：Win32Exception 从命令链逃逸即进程崩溃
    }

    [Fact]
    public void Settings_OpenConfigFolder_WhenPlatformThrows_DoesNotEscapeAndShowsToast()
    {
        using var ctx = VmFactory.Build(
            platformInfo: new FakePlatformInfo(isLinux: true) { ThrowOnOpenDirectory = true });
        ctx.Vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(ctx.Vm.CurrentPage);
        var toastsBefore = ctx.Vm.Toasts.Count;

        var ex = Record.Exception(() => settings.OpenConfigFolderCommand.Execute(null));

        Assert.Null(ex); // 红落此断言：Win32Exception 从命令链逃逸即进程崩溃
        Assert.Equal(toastsBefore + 1, ctx.Vm.Toasts.Count); // 失败必须可见，不是静默 no-op
    }
}
