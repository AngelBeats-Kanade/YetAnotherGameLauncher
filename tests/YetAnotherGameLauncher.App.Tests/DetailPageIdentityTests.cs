using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 详情页身份感与未安装空态：
/// 1) 信息簇以 chips 行为首（自绘标题 2026-09-25 移除：官方背景自带烧录 Logo 字标，
///    识别职责由窗口 Title「游戏名 · 应用名」与侧栏选中项承担）；
/// 2) 窗口 Title 跟随当前页（游戏页 =「游戏名 · 应用名」，其余页 = 应用名）；
/// 3) 未安装时空态引导卡可见、禁用的启动钮隐藏（规范 §5 空态要设计 / §6 隐藏不可用操作）。
/// </summary>
[Collection("sequential")]
public class DetailPageIdentityTests : IDisposable
{
    /// <summary>应用名后缀：WindowTitle 组装规则的一部分（与 MainWindowViewModel 常量一致）。</summary>
    public const string AppTitle = "YetAnotherGameLauncher";

    private readonly VmFactory.Context _ctx;

    public DetailPageIdentityTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task DetailPage_InfoCluster_LeadsWithChips_NoDrawnTitle()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        var chipsFirst = false;
        var statusTextY = -1.0;
        var hasDrawnTitle = true;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var page = window.GetVisualDescendants().OfType<Panel>()
                .First(p => p.Classes.Contains("page"));
            hasDrawnTitle = page.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == wuwa.DisplayName);

            // chips 行是信息簇首元素（自绘标题移除后，2026-09-25 用户决定）
            var statusText = page.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == wuwa.StatusText);
            statusTextY = statusText.TranslatePoint(new Point(0, 0), page)!.Value.Y;
            chipsFirst = statusTextY < 120;

            window.Close();
        }, CancellationToken.None);

        Assert.False(hasDrawnTitle, "详情页不得自绘游戏名标题（官方背景自带烧录 Logo 字标，自绘与之重复打架）");
        Assert.True(chipsFirst, $"状态 chips 应落在信息簇顶部（y<120），实测 y={statusTextY}");
    }

    [Fact]
    public async Task WindowTitle_FollowsCurrentPage()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        var gameTitle = "";
        var settingsTitle = "";
        var backTitle = "";
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            gameTitle = window.Title ?? "";
            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            settingsTitle = window.Title ?? "";
            _ctx.Vm.ShowGamesCommand.Execute(null);
            backTitle = window.Title ?? "";
            window.Close();
        }, CancellationToken.None);

        Assert.Equal($"{wuwa.DisplayName} · {AppTitle}", gameTitle);
        Assert.Equal(AppTitle, settingsTitle);
        Assert.Equal($"{wuwa.DisplayName} · {AppTitle}", backTitle);
    }

    [Fact]
    public async Task DetailPage_NotInstalled_ShowsEmptyStateCard_AndHidesLaunchButton()
    {
        await _ctx.Vm.InitializeAsync();
        Assert.False(_ctx.Vm.Games[0].IsInstalled); // 夹具前置：默认未安装

        var card = default(Border);
        var cardVisible = false;
        var launchHidden = false;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            card = window.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Name == "EmptyStateCard"); // 模板内具名元素不在窗口 namescope，走视觉树
            cardVisible = card?.IsVisible == true;
            var launch = window.GetVisualDescendants().OfType<Button>()
                .First(b => ReferenceEquals(b.Command, _ctx.Vm.Games[0].LaunchCommand));
            launchHidden = !launch.IsVisible;
            window.Close();
        }, CancellationToken.None);

        Assert.NotNull(card);
        Assert.True(cardVisible, "未安装态空态引导卡不可见");
        Assert.True(launchHidden, "未安装时禁用的启动钮应隐藏（安装钮已是主 CTA）");
    }

    [Fact]
    public async Task DetailPage_DetectedState_HidesEmptyStateCard_AndShowsLaunchButton()
    {
        // 复现（修复轮 code review）：detected 态（文件在、未登记）可直接启动，
        // 空态引导卡"尚未安装，请安装"与状态 chip/启动主钮同屏自相矛盾——卡片必须隐藏
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];
        var exePath = Path.Combine(
            wuwa.InstallDirPath, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllBytes(exePath, "MZ"u8.ToArray());
        await wuwa.RefreshAsync();
        Assert.True(wuwa.CanLaunch && !wuwa.IsInstalled); // 前置：确为 detected 态

        var cardVisible = true;
        var launchVisible = false;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var card = window.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Name == "EmptyStateCard");
            cardVisible = card?.IsVisible == true;
            var launch = window.GetVisualDescendants().OfType<Button>()
                .First(b => ReferenceEquals(b.Command, wuwa.LaunchCommand));
            launchVisible = launch.IsVisible;
            window.Close();
        }, CancellationToken.None);

        Assert.False(cardVisible, "detected 态显示安装引导卡：与状态 chip「可直接启动」同屏矛盾");
        Assert.True(launchVisible, "detected 态启动钮应可见");
    }

    [Fact]
    public async Task DetailPage_Installed_HidesEmptyStateCard_AndShowsLaunchButton()
    {
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.6.0" };
        await _ctx.Vm.InitializeAsync();
        Assert.False(_ctx.Vm.Games[0].IsInstalled);

        var zip = TestZip.Create(("Client/game.exe", "MZ"));
        _ctx.Kuro.Manifests["3.6.0"] = new GameManifest
        {
            Version = "3.6.0",
            Files =
            [
                new ManifestFile("Client/game.exe", zip.Length, Hashing.Md5Hex(zip), Url: "https://cdn/wuwa-full.zip"),
                new ManifestFile("Client/Binaries/Win64/Client-Win64-Shipping.exe", zip.Length, Hashing.Md5Hex(zip), Url: "https://cdn/wuwa-full.zip"),
            ],
        };
        _ctx.Downloader.Responses["https://cdn/wuwa-full.zip"] = zip;

        var card = default(Border);
        var cardHiddenAfterInstall = false;
        var launchVisible = false;
        var installed = false;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            PumpToCompletion(() => _ctx.Vm.Games[0].InstallOrUpdateCommand.ExecuteAsync(null));
            installed = _ctx.Vm.Games[0].IsInstalled;
            window.UpdateLayout();

            card = window.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Name == "EmptyStateCard"); // 模板内具名元素不在窗口 namescope，走视觉树
            cardHiddenAfterInstall = card?.IsVisible == false;
            var launch = window.GetVisualDescendants().OfType<Button>()
                .First(b => ReferenceEquals(b.Command, _ctx.Vm.Games[0].LaunchCommand));
            launchVisible = launch.IsVisible;
            window.Close();
        }, CancellationToken.None);

        Assert.NotNull(card);
        Assert.True(installed, "前置失败：安装流程未完成");
        Assert.True(cardHiddenAfterInstall, "安装完成后空态卡应隐藏");
        Assert.True(launchVisible, "安装完成后启动钮应可见");
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
