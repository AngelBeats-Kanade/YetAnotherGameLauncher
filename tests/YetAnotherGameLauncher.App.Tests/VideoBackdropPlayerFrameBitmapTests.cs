using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 帧位图生命周期回归（F5，2026-09-24 前 WriteableBitmap 从不 Dispose：尺寸重建/循环淡化
/// 归零/全清三处只丢引用，4K 上限约 33MB/张、14s 循环片泄漏约 140MB/分钟）。
/// 释放语义 = 退役宽限（<see cref="FfmpegVideoBackdropPlayer.RetireGrace"/>）后冲刷：
/// IBitmapImpl 仅 IDisposable 无引用计数（2026-09-24 核对 Avalonia master
/// src/Avalonia.Base/Platform/IBitmapImpl.cs），合成器可能仍持有在途引用，立即释放有
/// use-after-free 面——宽限期内不得释放也是本回归的一部分。
/// 位图创建/释放断言都在 headless 会话线程（AGENTS.md Dispatch 规则③）；不触碰 FFmpeg
/// 绑定（不经 PlayAsync），纯托管位图生命周期。
/// </summary>
[Collection("sequential")]
public class VideoBackdropPlayerFrameBitmapTests
{
    /// <summary>离线构造播放器（resolver 注入 stub client 与临时根，杜绝真实下载）。</summary>
    private static FfmpegVideoBackdropPlayer CreatePlayer() => new(new FfmpegLibraryResolver(
        new NetworkProxyManager(),
        downloadClient: new HttpClient(new StubHttpHandler()),
        downloadRoot: Path.Combine(Path.GetTempPath(), $"yagl-bitmap-tests-{Guid.NewGuid():N}")));

    [Fact]
    public async Task FrameBitmaps_ReleasedAfterGrace_OnSizeRebuildAndStop()
    {
        var originalGrace = FfmpegVideoBackdropPlayer.RetireGrace;
        FfmpegVideoBackdropPlayer.RetireGrace = TimeSpan.FromMilliseconds(60);
        FfmpegVideoBackdropPlayer player = null!;
        try
        {
            await HeadlessSession.Instance.Dispatch(() =>
            {
                player = CreatePlayer();
                player.FrameBitmapFactoryForTests = size => new WriteableBitmap(
                    size, new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Opaque);

                var first = player.EnsureFrameForTests(4, 4);
                Assert.NotNull(first);

                // 尺寸重建 → 旧位图退役（不得立即释放：宽限期保护在途渲染引用）
                var second = player.EnsureFrameForTests(8, 8);
                Assert.NotNull(second);
                Assert.Equal(2, player.CreatedFrameBitmapsForTests);
                player.FlushRetiredFrames();
                Assert.Equal(0, player.DisposedFrameBitmapsForTests);
            }, CancellationToken.None);

            await Task.Delay(120); // 越过宽限期

            await HeadlessSession.Instance.Dispatch(() =>
            {
                // 重建退役的旧位图：宽限后冲刷释放
                player.FlushRetiredFrames();
                Assert.Equal(1, player.DisposedFrameBitmapsForTests);

                // 全清（Stop）→ 当前帧同样退役；终末冲刷强制释放
                player.Stop();
                player.FlushRetiredFrames(force: true);
                Assert.Equal(2, player.DisposedFrameBitmapsForTests);
            }, CancellationToken.None);
        }
        finally
        {
            FfmpegVideoBackdropPlayer.RetireGrace = originalGrace;
        }
    }
}
