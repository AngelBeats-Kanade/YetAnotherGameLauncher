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
/// 详情页身份感与未安装空态（2026-09-23 UI 评审 P1-1/P1-2）：
/// 1) 详情页必须呈现游戏名（"沉浸影院"改版删除标题簇后页面无名，识别性缺口）；
/// 2) 窗口 Title 跟随当前页（游戏页 =「游戏名 · 应用名」，其余页 = 应用名）；
/// 3) 未安装时空态引导卡可见、禁用的启动钮隐藏（规范 §5 空态要设计 / §6 隐藏不可用操作）。
/// </summary>
[Collection("sequential")]
public class DetailPageIdentityTests : IDisposable
{
    /// <summary>应用名后缀：WindowTitle 组装规则的一部分（与 MainWindowViewModel 常量一致）。</summary>
    public const string AppTitle = "YetAnotherGameLauncher";

    private readonly VmFactory.Context _ctx;

    /// <summary>标题行是否落在 chips 上方（Dispatch 内赋值、Dispatch 外断言）。</summary>
    private bool TitleAboveChips;

    public DetailPageIdentityTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task DetailPage_ShowsGameTitle_AboveChips()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        var title = default(TextBlock);
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var page = window.GetVisualDescendants().OfType<Panel>()
                .First(p => p.Classes.Contains("page"));
            title = page.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Text == wuwa.DisplayName);

            // 标题行须位于状态 chips 上方（Row0 簇首元素）
            if (title is not null)
            {
                var statusText = page.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Text == wuwa.StatusText);
                TitleAboveChips = title!.TranslatePoint(new Point(0, 0), page)!.Value.Y
                                  < statusText.TranslatePoint(new Point(0, 0), page)!.Value.Y;
            }

            window.Close();
        }, CancellationToken.None);

        Assert.NotNull(title);
        Assert.True(TitleAboveChips, "游戏名未落在 chips 行上方（Row0 簇首）");
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
