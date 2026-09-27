using System.Diagnostics;
using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 背景视频播放器 Stop 的有界 join 契约（退出偶发 VAAPI "Failed to destroy decode context" 修复）：
/// 解码源的原生释放在各后台任务自己的线程 finally 里执行，Stop 若不等任务终止就返回，退出钩子
/// 返回后平台拆除与 avcodec_free_context 并发赛跑，vaDestroyContext 打在已拆的 display 上偶发报错。
/// 泊车缝模拟任务卡在不可取消的原生调用里（门等待不观察取消令牌），两条路径各有用例：
/// 门放行（join 生效）与预算超时（放行不悬挂退出）。任务体全捕异常，join 的 Wait 观察不到 fault。
/// </summary>
[Collection("sequential")]
public class VideoBackdropPlayerStopJoinTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    /// <summary>构造离线 resolver 的真实播放器（同 VideoBackdropPlayerCtsTests：stub 404 + 临时根目录）。</summary>
    private FfmpegVideoBackdropPlayer CreatePlayer() => new(new FfmpegLibraryResolver(
        new NetworkProxyManager(),
        downloadClient: new HttpClient(new StubHttpHandler()),
        downloadRoot: _tempDir.FilePath("ffmpeg-root")));

    /// <summary>非媒体文件：PlayAsync 走真实打开路径且必然快速失败，不依赖任何真库。</summary>
    private static string WriteNotMediaFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yagl-notmedia-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "not a video");
        return path;
    }

    [Fact]
    public async Task Stop_BlocksUntilSessionTaskTerminates()
    {
        var player = CreatePlayer();
        using var gate = new ManualResetEventSlim(false);
        player.SessionStartGateForTests = gate;
        var notMedia = WriteNotMediaFile();
        try
        {
            // PlayAsync 在首个 await 前同步完成任务派生与句柄捕获：调用返回即句柄已就位
            var playTask = player.PlayAsync(notMedia);
            var sessionTask = player.SessionTaskForTests;
            Assert.NotNull(sessionTask);

            var stopReturned = new ManualResetEventSlim(false);
            var stopTask = Task.Run(() =>
            {
                player.Stop();
                stopReturned.Set();
            });

            // 修复前 Stop 只 Cancel 即返回（无 join）→ 本断言红；修复后 Stop 阻塞在 join 上，
            // 直到门放行、任务终止。200ms 内不得返回（泊车任务在门放行前不可能终止）
            Assert.False(stopReturned.Wait(TimeSpan.FromMilliseconds(200)),
                "Stop 必须等待会话任务终止（有界 join）后才返回");

            gate.Set();
            Assert.True(stopReturned.Wait(TimeSpan.FromSeconds(10)), "门放行后 join 应立即收敛");
            Assert.True(sessionTask.IsCompleted, "Stop 返回时会话任务必须已终止（原生释放随之完成）");
            Assert.False(await playTask);
        }
        finally
        {
            gate.Set();
            File.Delete(notMedia);
        }
    }

    [Fact]
    public async Task Stop_JoinBudgetTimeout_ProceedsWithoutHanging()
    {
        var previous = FfmpegVideoBackdropPlayer.NativeReleaseJoinTimeout;
        FfmpegVideoBackdropPlayer.NativeReleaseJoinTimeout = TimeSpan.FromMilliseconds(150);
        try
        {
            var player = CreatePlayer();
            using var gate = new ManualResetEventSlim(false);
            player.SessionStartGateForTests = gate;
            var notMedia = WriteNotMediaFile();
            try
            {
                var playTask = player.PlayAsync(notMedia);
                var sessionTask = player.SessionTaskForTests;
                Assert.NotNull(sessionTask);

                var sw = Stopwatch.StartNew();
                var stopException = Record.Exception(player.Stop);
                sw.Stop();

                Assert.Null(stopException); // 超时放行：不抛、不悬挂退出
                Assert.False(sessionTask.IsCompleted, "泊车任务未终止说明走的是超时放行分支");
                Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(100), "Stop 应等待过 join 预算");

                gate.Set();
                Assert.False(await playTask);
            }
            finally
            {
                gate.Set();
                File.Delete(notMedia);
            }
        }
        finally
        {
            FfmpegVideoBackdropPlayer.NativeReleaseJoinTimeout = previous;
        }
    }
}
