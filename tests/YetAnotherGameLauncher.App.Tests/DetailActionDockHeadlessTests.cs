using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 操作坞的忙碌态语义（2026-10-18 用户点名「下载/更新时把启动游戏按钮禁用」）：
/// 更新进行中启动钮必须**禁用**（此前只有命令内 IsBusy 早退——外观仍可点，误触像"什么都没发生"）；
/// 更新结束后恢复可点。禁用观感由既有 <c>accent-onart:disabled</c> 样式承担（卡底 + 次级前景）。
/// 注意：窗口挂接期间 VM 状态只能在会话线程驱动（skills avalonia-headless-testing §4），
/// 因此整个"挂住更新 → 读按钮 → 放行 → 读按钮"都在同一个 Dispatch 内用 RunJobs 泵完成。
/// </summary>
[Collection("sequential")]
public class DetailActionDockHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public DetailActionDockHeadlessTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    /// <summary>取操作坞里的启动钮（模板内元素不在窗口 namescope：按命令引用匹配，同 DetailPageIdentityTests 先例）。</summary>
    private static Button FindLaunchButton(Window window, object command) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(button => ReferenceEquals(button.Command, command));

    /// <summary>在会话线程内泵异步续体直到条件成立（先例 BackgroundImageServiceTests.RunToCompletion）。</summary>
    private static void PumpUntil(Func<bool> condition, int timeoutMilliseconds = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("等待更新流程的异步续体超时");
            }

            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
    }

    [Fact]
    public async Task UpdateInProgress_DisablesLaunchButton_AndReenablesAfterCompletion()
    {
        var zipBytes = TestZip.Create(("Client/game.exe", "MZ"));
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.6.0" };
        _ctx.Kuro.Manifests["3.6.0"] = new GameManifest
        {
            Version = "3.6.0",
            Files =
            [
                new ManifestFile("Client/game.exe", zipBytes.Length, Hashing.Md5Hex(zipBytes), Url: "https://cdn/game.exe"),
                new ManifestFile("Client/Binaries/Win64/Client-Win64-Shipping.exe", zipBytes.Length, Hashing.Md5Hex(zipBytes), Url: "https://cdn/game.exe"),
            ],
        };
        _ctx.Downloader.Responses["https://cdn/game.exe"] = zipBytes;
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        // 前置：已安装可启动（exe 在盘）+ 有可用更新（本地 3.5.0 → 最新 3.6.0）
        var exePath = Path.Combine(
            wuwa.InstallDirPath, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        await File.WriteAllBytesAsync(exePath, "MZ"u8.ToArray());
        await new LocalStateService(wuwa.InstallDirPath).SaveAsync(
            new LocalGameState { GameId = wuwa.Game.Id, ServerId = wuwa.SelectedServer.Id, Version = "3.5.0" });
        await wuwa.RefreshAsync();
        Assert.True(wuwa.CanLaunch);
        Assert.True(wuwa.HasUpdate);

        var enabledDuringUpdate = false;
        var enabledAfterUpdate = false;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();
            var launch = FindLaunchButton(window, wuwa.LaunchCommand);
            Assert.True(launch.IsEnabled, "更新开始前启动按钮应可点（测试前置）");

            // 把更新挂在拉清单处：IsBusy 已置位、下载尚未结束——正是用户看得到进度条的窗口
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reachedManifestFetch = 0;
            _ctx.Kuro.ManifestHandler = async (_, version, _) =>
            {
                Interlocked.Increment(ref reachedManifestFetch);
                await gate.Task;
                return _ctx.Kuro.Manifests[version];
            };

            var update = wuwa.InstallOrUpdateCommand.ExecuteAsync(null);
            PumpUntil(() => Volatile.Read(ref reachedManifestFetch) == 1);
            Assert.True(wuwa.IsBusy, "更新入口应置忙碌态（测试前置）");
            enabledDuringUpdate = launch.IsEnabled;

            gate.SetResult();
            PumpUntil(() => update.IsCompleted);
            window.UpdateLayout();
            enabledAfterUpdate = launch.IsEnabled;
            window.Close();
        }, CancellationToken.None);

        Assert.False(enabledDuringUpdate, "下载/更新进行中启动按钮仍可点：应禁用（用户点名 2026-10-18）");
        Assert.True(enabledAfterUpdate, "更新结束后启动按钮应恢复可点");
    }
}
