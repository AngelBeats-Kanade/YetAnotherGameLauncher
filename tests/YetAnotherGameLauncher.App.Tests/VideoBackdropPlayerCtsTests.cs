using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 背景视频播放器 cts 生命周期回归（2026-09-20 复审）：自然结束（循环自愈退出/解码失败）路径
/// 不经过 Cancel，收尾若只释放局部 cts 不摘共享引用，_cts 悬挂已释放实例，
/// 下一次 Stop()（切页/切游戏的 SetDetailActive→StopVideo）对其 Cancel 抛 ObjectDisposedException。
/// resolver 一律注入 stub client + 临时根目录（2026-09-22）：新环境（无系统库/无缓存）不再真实
/// 下载约 60–70MB、不写真实用户数据目录——库缺失时走 stub 404 快速失败，库可用机器照常真绑定。
/// </summary>
[Collection("sequential")]
public class VideoBackdropPlayerCtsTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    /// <summary>构造离线 resolver 的真实播放器：下载出口只有 stub（未映射一律 404），
    /// 下载/缓存探测根指向临时空目录。</summary>
    private FfmpegVideoBackdropPlayer CreatePlayer() => new(new FfmpegLibraryResolver(
        new NetworkProxyManager(),
        downloadClient: new HttpClient(new StubHttpHandler()),
        downloadRoot: _tempDir.FilePath("ffmpeg-root")));

    [Fact]
    public async Task NaturalFailure_ClearsSharedCtsReference_AndStopDoesNotThrow()
    {
        var player = CreatePlayer();
        var notMedia = Path.Combine(Path.GetTempPath(), $"yagl-notmedia-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(notMedia, "not a video");
        try
        {
            // 非媒体文件在"库可用"与"库缺失"两种环境都走失败路径（解码打开失败/EnsureReady false），
            // PlayAsync 稳定返回 false
            Assert.False(await player.PlayAsync(notMedia));

            // 后台任务的 finally 与 PlayAsync 的返回续体并发：以摘除标志确定性等待落定，
            // 而不是猜固定延迟——修复前该标志永不置位，等待超时即红
            var cleared = false;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (player.CtsClearedForTest)
                {
                    cleared = true;
                    break;
                }

                await Task.Delay(10);
            }

            Assert.True(cleared);
            var stopped = Record.Exception(player.Stop);
            Assert.Null(stopped);
        }
        finally
        {
            File.Delete(notMedia);
        }
    }

    [Fact]
    public void Stop_WithAlreadyDisposedCts_DoesNotThrowAndClearsFrame()
    {
        // 确定性复现：直接布置已释放的 cts，Stop 必须吞掉 ObjectDisposedException 并照常清帧
        var player = CreatePlayer();
        var cts = new CancellationTokenSource();
        cts.Dispose();
        player.CtsForTest = cts;

        var stopped = Record.Exception(player.Stop);

        Assert.Null(stopped);
        Assert.True(player.CtsClearedForTest);
    }

    [Fact]
    public void PauseResume_WithoutActiveSession_AreNoOps()
    {
        // 无会话时 Pause/Resume 必须是无害 no-op（Pause 尤其不得留下复位门——
        // 否则下一次 PlayAsync 起播即泊车永不产帧；新会话启动时 Set 门兜底另有一层防线）
        var player = CreatePlayer();

        Assert.False(player.IsSessionActive);

        var thrown = Record.Exception(() =>
        {
            player.Pause();
            player.Resume();
            player.Pause();
        });

        Assert.Null(thrown);
        Assert.False(player.IsSessionActive);
    }
}
