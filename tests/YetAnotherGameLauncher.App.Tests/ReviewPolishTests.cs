using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 收尾打磨批次（2026-09-23 UI 评审 P2-11 / P3-13 / P3-15）：
/// 1) 侧栏状态行使用短变体文案（EN 长文案截断成语法错误的词干）；
/// 2) 启动失败覆盖层在场时挂起新 toast（瞬态消息不得压过模态错误）；
/// 3) 关于页提供项目主页出口（经平台缝打开浏览器，测试注入假平台记录调用）。
/// </summary>
[Collection("sequential")]
public class ReviewPolishTests : IDisposable
{
    private readonly FakePlatformInfo _platform;
    private readonly VmFactory.Context _ctx;

    public ReviewPolishTests()
    {
        _platform = new FakePlatformInfo(isLinux: false);
        _ctx = VmFactory.Build(platformInfo: _platform);
    }

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task SidebarStatus_UsesShortVariant_WhenAvailable()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        // 造"检测到游戏文件"态（文件在、未登记）：EN 全文案 39 字符必然截断，侧栏用短变体
        var exePath = Path.Combine(
            wuwa.InstallDirPath, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllBytes(exePath, "MZ"u8.ToArray());
        await wuwa.RefreshAsync();

        Assert.NotEqual(wuwa.StatusText, wuwa.SidebarStatusText);
        Assert.Equal(_ctx.Vm.Loc["status_detectedShort"], wuwa.SidebarStatusText);
    }

    [Fact]
    public async Task SidebarStatus_NotInstalled_UsesExistingShortVariant()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        // notInstalled 的 zh 全文案"尚未安装"与既有 Short 键"未安装"本就不同，侧栏应取 Short
        Assert.NotEqual(wuwa.StatusText, wuwa.SidebarStatusText);
        Assert.Equal(_ctx.Vm.Loc["status_notInstalledShort"], wuwa.SidebarStatusText);
    }

    [Fact]
    public async Task ShowToast_Suppressed_WhileLaunchErrorOverlayIsUp()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        Assert.Empty(_ctx.Vm.Toasts);
        wuwa.LaunchError = new LaunchErrorViewModel(
            "启动失败", "detail", null, platform: _platform, canRetry: false);
        _ctx.Vm.ShowToast("鸣潮", "状态变化提示", ToastKind.Info);
        Assert.Empty(_ctx.Vm.Toasts);

        wuwa.LaunchError = null; // 覆盖层退场后 toast 恢复
        _ctx.Vm.ShowToast("鸣潮", "状态变化提示", ToastKind.Info);
        Assert.Single(_ctx.Vm.Toasts);
    }

    [Fact]
    public async Task AboutPage_ProjectHomeCommand_OpensRepositoryUrl()
    {
        await _ctx.Vm.InitializeAsync();
        _ctx.Vm.ShowAboutCommand.Execute(null);
        var about = Assert.IsType<AboutViewModel>(_ctx.Vm.CurrentPage);

        about.OpenProjectHomeCommand.Execute(null);

        Assert.Contains(AboutViewModel.ProjectHomeUrl, _platform.OpenedUrls);
    }
}
