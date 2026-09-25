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

    [Fact]
    public void Dispose_ReleasesResumeGate()
    {
        // F37（artifacts/bugs.md）：Dispose() => StopCore() 从不释放 _resumeGate——
        // per-game 实例列表重建时整批废弃，WaitHandle 懒创建的内核句柄累积。
        // 修复后 Dispose 释放 gate，后续访问抛 ObjectDisposedException
        var player = CreatePlayer();
        player.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = player.ResumeGateForTests.WaitHandle);
    }

    [Fact]
    public async Task ClearFrame_TerminalFlush_RespectsGraceForLaterRetirements()
    {
        // F36（artifacts/bugs.md）：ClearFrame 调度 RetireGrace+1s 后的终末冲刷曾用 force——
        // Stop→Play 快速序列里，新一代会话在 (调度, 执行) 窗口内退役的位图 age<grace 也被
        // 强释，恰是 d81aaa5 要消灭的 use-after-free。修复后终末冲刷走宽限检查形态：
        // 调度前退役的必然释放（无泄漏不变），调度后退役的按自身宽限走。
        // 时间线（grace=200ms，终末任务 T0+1200ms 触发）：B 在 T0+1100ms 退役（age 100ms），
        // T0+1250ms 后检查：调度前退役的 A 已释放、B 必须仍活着（红形态=force 下 A+B 全释放）；
        // T0+1500ms 后 B 宽限满，自然释放。
        var originalGrace = FfmpegVideoBackdropPlayer.RetireGrace;
        FfmpegVideoBackdropPlayer.RetireGrace = TimeSpan.FromMilliseconds(200);
        FfmpegVideoBackdropPlayer player = null!;
        try
        {
            await HeadlessSession.Instance.Dispatch(() =>
            {
                player = CreatePlayer();
                player.FrameBitmapFactoryForTests = size => new WriteableBitmap(
                    size, new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Opaque);
                player.EnsureFrameForTests(4, 4);
                player.Stop(); // T0：退役 A + 调度终末冲刷（T0+1200ms）
                Assert.Equal(0, player.DisposedFrameBitmapsForTests);
            }, CancellationToken.None);

            var t0 = DateTime.UtcNow;
            await Task.Delay(TimeSpan.FromMilliseconds(1100)); // T0+1100ms

            await HeadlessSession.Instance.Dispatch(() =>
            {
                player.EnsureFrameForTests(4, 4); // 重建
                player.EnsureFrameForTests(8, 8); // 尺寸重建：退役 B（≈T0+1100ms）
                Assert.Equal(0, player.DisposedFrameBitmapsForTests);
            }, CancellationToken.None);

            var elapsed = DateTime.UtcNow - t0;
            if (elapsed < TimeSpan.FromMilliseconds(1200))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1200) - elapsed); // 越过终末触发点
            }

            await Task.Delay(80); // ≈T0+1280ms：A（age≫grace）已释放，B（age≈180ms<grace）必须还在
            var countAtCheckpoint = 0;
            await HeadlessSession.Instance.Dispatch(() =>
            {
                countAtCheckpoint = player.DisposedFrameBitmapsForTests;
            }, CancellationToken.None);
            Assert.Equal(1, countAtCheckpoint); // 红落此断言：force 形态 A+B 全释放 = 2

            await Task.Delay(400); // ≈T0+1680ms：B 宽限满，终末任务（宽限检查）释放之
            var countAtEnd = 0;
            await HeadlessSession.Instance.Dispatch(() =>
            {
                player.FlushRetiredFrames();
                countAtEnd = player.DisposedFrameBitmapsForTests;
            }, CancellationToken.None);
            Assert.Equal(2, countAtEnd); // 终态：A 与 B 都释放（无泄漏保证不变）
        }
        finally
        {
            FfmpegVideoBackdropPlayer.RetireGrace = originalGrace;
        }
    }
}
