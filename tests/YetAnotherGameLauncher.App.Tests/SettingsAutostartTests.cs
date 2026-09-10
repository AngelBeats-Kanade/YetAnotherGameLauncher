using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 设置页与自启状态的集成回归：曾因 IsAutostart 绑定在 UI 线程同步等待
/// 自启查询子进程（sync-over-async）导致点击"设置"整窗死锁。现全部异步化，
/// 此处用各平台真实实现（Windows 注册表查询 / Linux XDG desktop 文件）确保链路不阻塞、能出结果。
/// </summary>
[Collection("sequential")]
public class SettingsAutostartTests
{
    [Fact]
    public async Task ShowSettings_WithRealRegistryQuery_InitializesAutostartState()
    {
        // 平台分支注入真实实现：Windows 真查 HKCU Run；Linux 用真实 XDG 实现（home 指向临时目录）
        using var tempHome = new TempDir();
        IAutostartService service = OperatingSystem.IsWindows()
            ? new WindowsAutostartService(new SystemProcessRunner())
            : new LinuxAutostartService(home: tempHome.Path);
        using var ctx = VmFactory.Build(autostart: service);
        await ctx.Vm.InitializeAsync();

        ctx.Vm.ShowSettingsCommand.Execute(null);

        var page = await WaitForAsync<SettingsViewModel>(
            () => ctx.Vm.CurrentPage as SettingsViewModel, TimeSpan.FromSeconds(15));
        Assert.NotNull(page);

        // 打开设置页时异步补齐自启状态（本机未注册自启 = false），不再卡死
        var settled = await WaitForConditionAsync(
            () => page.IsAutostart is false, TimeSpan.FromSeconds(15));
        Assert.True(settled);
    }

    [Fact]
    public async Task AutostartService_QueryRealRegistry_Completes()
    {
        // 各平台真实实现只读查询：测试环境未写自启键，应返回 false（Windows 键不存在 = exit 1；
        // Linux autostart 目录为空）
        using var tempHome = new TempDir();
        IAutostartService service = OperatingSystem.IsWindows()
            ? new WindowsAutostartService(new SystemProcessRunner())
            : new LinuxAutostartService(home: tempHome.Path);
        Assert.False(await service.IsEnabledAsync());
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is T value)
            {
                return value;
            }

            await Task.Delay(50);
        }

        return null!;
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }
}
