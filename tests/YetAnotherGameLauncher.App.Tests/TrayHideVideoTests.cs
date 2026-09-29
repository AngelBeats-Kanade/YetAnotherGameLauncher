using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 驻留隐藏路径的视频语义（0.1.3 review F1 回归）：
/// 隐藏不得对"切页已泊车"的页面二次 <see cref="ViewModels.GameItemViewModel.SuspendVideo"/>——
/// 它会清 _pendingVideoPath，泊车期间重解析写入的待播路径被抹掉后，重进详情页走续播
/// 快路径复活旧区域/旧版本视频（F27 同族）。正路径：详情页在播时隐藏 = 暂停保活，
/// 托盘唤回续播（不重新起播）。
/// </summary>
[Collection("sequential")]
public class TrayHideVideoTests
{
    [Fact]
    public async Task HideToTray_WhileParkedWithReResolvedPending_PreservesPendingForNextEntry()
    {
        var tempDir = new TestSupport.TempDir();
        try
        {
            // 每游戏独立播放器（VmFactory 默认）：共享单例会让另一游戏（未桩 resolver、
            // 走静态图分支的 StopVideo）打死本用例观察的会话——夹具曾因此假红。
            // Players 列表在游戏 VM 构造（InitializeAsync 内 RebuildGames）时才填充，
            // 引用必须在其后取
            using var ctx = VmFactory.Build();
            var videoA = tempDir.FilePath("cached", "backdrop-a.mp4");
            var videoB = tempDir.FilePath("cached", "backdrop-b.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(videoA)!);
            await File.WriteAllTextAsync(videoA, "fake");
            await File.WriteAllTextAsync(videoB, "fake");
            // 构造机器同 F27 用例（VideoBackdropHeadlessTests）：resolver 计数定位重解析
            // 已发起，宽裕延时等 pending 写入（其在重解析同步尾部）
            var resolverCalls = 0;
            ctx.KuroBackdrop.Resolver = _ => { resolverCalls++; return new BackdropSource(videoA, BackdropKind.Video); };
            await ctx.Vm.InitializeAsync();
            var player = ctx.Players[0];
            var game = ctx.Vm.Games[0];
            await WaitUntil(() => player.PlayedPaths.Count >= 1); // 预热重解析 + 导航激活起播 A
            Assert.Equal([videoA], player.PlayedPaths);

            // 切去设置页泊车（会话存活）→ 版本翻转 + 背景换 B：重解析只写 pending 不停旧会话
            ctx.Vm.ShowSettingsCommand.Execute(null);
            ctx.KuroBackdrop.Resolver = _ => { resolverCalls++; return new BackdropSource(videoB, BackdropKind.Video); };
            ctx.Kuro.VersionInfo = new Core.Models.ChannelVersionInfo { LatestVersion = "2.1.0" };
            game.ResetVersionCheckCache(); // 版本检测每启动至多一次（会话缓存）——清掉才见翻转版本
            await game.RefreshAsync();
            await WaitUntil(() => resolverCalls >= 2);
            await Task.Delay(200); // 重解析收尾（pending 写入在其同步尾部）

            // 驻留隐藏（等价 HideToTray 模式点关闭的 VM 路径）：不得抹掉泊车页的 pending
            ctx.Vm.OnWindowHiddenToTray(800, 600, false);

            // 重进详情页：必须起新会话播 B（红落此断言：pending 被清后走 Resume 复活 A）
            game.SetDetailActive(true);
            await WaitUntil(() => player.PlayedPaths.Count >= 2);
            Assert.Equal([videoA, videoB], player.PlayedPaths);
            Assert.Equal(0, player.ResumeCount); // 快路径本次不得被走（有更新待播）
        }
        finally
        {
            tempDir.Dispose();
        }
    }

    [Fact]
    public async Task HideToTray_WhileDetailActive_PausesKeepAlive_AndRestoreResumes()
    {
        var tempDir = new TestSupport.TempDir();
        try
        {
            using var ctx = VmFactory.Build(); // 每游戏独立播放器，避免另一游戏的 Stop 串台
            var localVideo = tempDir.FilePath("cached", "backdrop.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(localVideo)!);
            await File.WriteAllTextAsync(localVideo, "fake");
            ctx.KuroBackdrop.Resolver = _ => new BackdropSource(localVideo, BackdropKind.Video);
            await ctx.Vm.InitializeAsync();
            var player = ctx.Players[0]; // Players 在 RebuildGames 时填充，须在 InitializeAsync 后取
            var game = ctx.Vm.Games[0];
            await WaitUntil(() => player.PlayedPaths.Count >= 1);
            // 初始化导航后 CurrentPage 即该游戏详情页（InitializeAsync 末尾 NavigateTo(SelectedGame)）
            Assert.Same(game, ctx.Vm.CurrentPage);

            // 在播时驻留隐藏：暂停保活而非停止
            ctx.Vm.OnWindowHiddenToTray(800, 600, false);
            Assert.Equal(1, player.PauseCount);
            var stops = player.StopCount;
            Assert.True(player.IsSessionActive);

            // 托盘唤回：续播快路径（Resume 一次、不重新起播、不停止）
            ctx.Vm.OnWindowRestoredFromTray();
            Assert.Equal(1, player.ResumeCount);
            Assert.Equal(stops, player.StopCount);
            Assert.Single(player.PlayedPaths);
        }
        finally
        {
            tempDir.Dispose();
        }
    }

    /// <summary>有界轮询至条件成立（起播经弃元后台任务 + 延迟窗口，不能同步等待）。</summary>
    private static async Task WaitUntil(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "有界轮询超时：条件未在期限内成立");
    }
}
