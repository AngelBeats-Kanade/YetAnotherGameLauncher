using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 背景视频链路无头回归：解析器返回视频源时，进入详情页起播（fake 播放器）、
/// 首帧到达后视频层可见、切走/关页停止；静态图来源不触碰播放器。
/// </summary>
[Collection("sequential")]
public class VideoBackdropHeadlessStartTests
{
    [Fact]
    public async Task StartVideo_OlderPendingCallFailingLater_DoesNotKillNewerPlayback()
    {
        // 回归（2026-09-20 复审）：起播窗口内被后发起播抢先时，旧调用的 finally 失败兜底
        // StopVideo 是 VM 级全局停止，会误杀新一代起播——视频层不再点亮直到离页再进。
        // 场景载体：详情页在播窗口内切语言触发 LoadAssetsCoreAsync 再起播。
        var tempDir = new TestSupport.TempDir();
        try
        {
            var player = new VmFactory.FakeVideoPlayer();
            using var ctx = VmFactory.Build(videoPlayer: player);
            var localVideo = tempDir.FilePath("cached", "backdrop.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(localVideo)!);
            await File.WriteAllTextAsync(localVideo, "fake");
            ctx.KuroBackdrop.Resolver = _ => new BackdropSource(localVideo, BackdropKind.Video);
            await ctx.Vm.InitializeAsync();
            var game = ctx.Vm.Games[0];

            var calls = 0;
            var firstPlayGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.AsyncPlayHandler = _ =>
            {
                calls++;
                return calls == 1 ? firstPlayGate.Task : Task.FromResult(true);
            };

            // 第一次起播挂在起播窗口内；随后第二次起播（先停旧的、再成功）
            game.SetDetailActive(true);
            Assert.Equal(1, calls);
            game.SetDetailActive(false);
            game.SetDetailActive(true);
            Assert.Equal(2, calls);
            var stopCountAfterSecondStart = player.StopCount;

            // 第二次起播点亮视频层（假帧即可：只判非空，不触渲染接口——Bitmap 须在会话线程）
            player.Frame = new StubImage();
            player.RaiseFrame();
            Assert.True(game.HasBackgroundVideo);

            // 第一次起播此时失败返回：不得停掉新一代播放
            firstPlayGate.SetResult(false);
            await Task.Delay(100);

            Assert.Equal(stopCountAfterSecondStart, player.StopCount);
            Assert.True(game.HasBackgroundVideo);
        }
        finally
        {
            tempDir.Dispose();
        }
    }

    /// <summary>非空占位帧：仅用于"帧缓冲非空才点亮"判定，不触平台渲染接口。</summary>
    private sealed class StubImage : Avalonia.Media.IImage
    {
        public Avalonia.Size Size => new(4, 4);

        public void Draw(Avalonia.Media.DrawingContext context, Avalonia.Rect sourceRect, Avalonia.Rect destRect)
        {
        }
    }
}

/// <summary>
/// 背景视频链路无头回归（UI 线程）：解析器返回视频源时，进入详情页起播（fake 播放器）、
/// 首帧到达后视频层可见、切走/关页停止；静态图来源不触碰播放器。
/// </summary>
[Collection("sequential")]
public class VideoBackdropHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;
    private readonly VmFactory.FakeVideoPlayer _player = new();

    public VideoBackdropHeadlessTests()
    {
        _ctx = VmFactory.Build(videoPlayer: _player);
    }

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task VideoSource_PlaysWhenDetailVisible_StopsWhenLeaving()
    {
        var localVideo = _ctx.TempDir.FilePath("cached", "backdrop.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(localVideo)!);
        await File.WriteAllTextAsync(localVideo, "fake");
        _ctx.KuroBackdrop.Resolver = _ => new BackdropSource(localVideo, BackdropKind.Video);

        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var game = _ctx.Vm.Games[0];

            // 进入详情页：待播视频立即起播
            _ctx.Vm.GameNavSelection = game;
            window.UpdateLayout();
            Assert.Equal([localVideo], _player.PlayedPaths);

            // 首帧到达：视频层可见（位图随播放器存续，不能 using 释放——Render 仍会访问）
            var frame = new WriteableBitmap(
                new Avalonia.PixelSize(4, 4), new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormats.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
            _player.Frame = frame;
            _player.RaiseFrame();
            window.UpdateLayout();
            Assert.True(game.HasBackgroundVideo);
            AssertSurfaceVisible(window, game, expected: true);

            // 切到设置页：视频停止且层隐藏
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            Assert.True(_player.StopCount >= 1);
            Assert.False(game.HasBackgroundVideo);
            AssertSurfaceVisible(window, game, expected: false);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task VideoSource_ReentersDetailPage_ResumesPlayback()
    {
        var localVideo = _ctx.TempDir.FilePath("cached", "backdrop.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(localVideo)!);
        await File.WriteAllTextAsync(localVideo, "fake");
        _ctx.KuroBackdrop.Resolver = _ => new BackdropSource(localVideo, BackdropKind.Video);

        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var game = _ctx.Vm.Games[0];
            _ctx.Vm.GameNavSelection = game;
            window.UpdateLayout();
            Assert.Equal([localVideo], _player.PlayedPaths);

            // 切到设置页停播；再切回同一游戏详情：不重新解析背景也应凭已解析路径恢复播放
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            var playsAfterLeave = _player.PlayedPaths.Count;

            _ctx.Vm.ShowGamesCommand.Execute(null);
            window.UpdateLayout();
            Assert.True(_player.PlayedPaths.Count > playsAfterLeave);
            Assert.Equal(localVideo, _player.PlayedPaths[^1]);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ImageSource_PlayerNeverInvoked()
    {
        _ctx.KuroBackdrop.Resolver = _ => new BackdropSource("https://cdn.example.com/bg.png", BackdropKind.Image);

        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            _ctx.Vm.GameNavSelection = _ctx.Vm.Games[0];
            window.UpdateLayout();

            Assert.Empty(_player.PlayedPaths);
            Assert.False(_ctx.Vm.Games[0].HasBackgroundVideo);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task VideoSource_PlayAsyncThrows_FallsBackToPosterWithoutCrash()
    {
        // async review 回归（2026-09-19）：生产播放器是原生 FFmpeg 解码，PlayAsync 可能抛
        //（驱动重置/库缺失）。StartVideoAsync 被 _ = 弃元调用，异常必须被方法内 catch 兜住——
        // 视频层不点亮、海报兜底、测试进程不崩（本用例正常跑完即断言了"未崩"）。
        var localVideo = _ctx.TempDir.FilePath("cached", "backdrop.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(localVideo)!);
        await File.WriteAllTextAsync(localVideo, "fake");
        _ctx.KuroBackdrop.Resolver = _ => new BackdropSource(localVideo, BackdropKind.Video);
        _player.PlayHandler = _ => throw new InvalidOperationException("decoder init failed");

        await _ctx.Vm.InitializeAsync();

        var playedPaths = new List<string>();
        var videoLit = true;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            _ctx.Vm.GameNavSelection = _ctx.Vm.Games[0];
            window.UpdateLayout();

            playedPaths.AddRange(_player.PlayedPaths);
            videoLit = _ctx.Vm.Games[0].HasBackgroundVideo;
            window.Close();
        }, CancellationToken.None);

        // 断言在 Dispatch 外
        Assert.Equal([localVideo], playedPaths); // 起播确实尝试过
        Assert.False(videoLit); // 抛异常后视频层不点亮（海报兜底）
        Assert.Null(_player.Frame); // 共享帧缓冲无残留帧
    }

    [Fact]
    public async Task VideoSource_FailedStart_DetachesFrameNotification()
    {
        // M7 残余收拢（2026-09-19）：起播失败必须退订 FrameUpdated，否则共享播放器后续产出的帧
        // （含上一游戏残帧的迟到通知）会点亮本页视频层。探针：失败后注入非空帧再手动触发通知——
        // 已退订则 HasBackgroundVideo 恒 false；悬挂订阅会把它点亮（M7 的旧论证里 Frame 恒 null，
        // 行为无差异、探测不到悬挂订阅，这里注入帧才让泄漏可见）。
        var localVideo = _ctx.TempDir.FilePath("cached", "backdrop.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(localVideo)!);
        await File.WriteAllTextAsync(localVideo, "fake");
        _ctx.KuroBackdrop.Resolver = _ => new BackdropSource(localVideo, BackdropKind.Video);
        _player.PlayHandler = _ => throw new InvalidOperationException("decoder init failed");

        await _ctx.Vm.InitializeAsync();

        var videoLit = true;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            _ctx.Vm.GameNavSelection = _ctx.Vm.Games[0];
            window.UpdateLayout(); // 起播同步失败（桩抛异常在首个 await 前）：finally 已退订

            var frame = new WriteableBitmap(
                new Avalonia.PixelSize(4, 4), new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormats.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
            _player.Frame = frame;
            _player.RaiseFrame(); // 迟到通知：悬挂订阅会在此点亮视频层
            videoLit = _ctx.Vm.Games[0].HasBackgroundVideo;
            window.Close();
        }, CancellationToken.None);

        Assert.False(videoLit); // 退订缺失即红
    }

    [Fact]
    public async Task VideoSource_SwitchingGames_LateStaleNotifyDoesNotLightNewGame()
    {
        // 两个游戏共享一个播放器（与生产 DI 单例一致）。切游戏后，上一游戏解码循环"最后一帧"
        // 的帧通知经 UI 线程 Dispatcher 异步投递，可能落在新游戏订阅之后——若共享帧缓冲仍持有
        // 旧帧，新详情页会点亮视频层并短暂显示上一游戏的画面（背景残留）。
        // 契约：Stop 必须清空帧缓冲，迟到的陈旧通知以空帧缓冲为证不再点亮。
        var videoA = _ctx.TempDir.FilePath("cached", "a.mp4");
        var videoB = _ctx.TempDir.FilePath("cached", "b.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(videoA)!);
        await File.WriteAllTextAsync(videoA, "fake");
        await File.WriteAllTextAsync(videoB, "fake");
        _ctx.KuroBackdrop.Resolver = _ => new BackdropSource(videoA, BackdropKind.Video);
        _ctx.GryphlineBackdrop.Resolver = _ => new BackdropSource(videoB, BackdropKind.Video);

        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var a = _ctx.Vm.Games[0];
            var b = _ctx.Vm.Games[1];

            // A 详情页起播并出首帧：视频层可见
            _ctx.Vm.GameNavSelection = a;
            window.UpdateLayout();
            Assert.Equal([videoA], _player.PlayedPaths);
            _player.Frame = NewFrame();
            _player.RaiseFrame();
            window.UpdateLayout();
            Assert.True(a.HasBackgroundVideo);

            // 切到 B：A 停止且共享帧缓冲清空；B 起播但首帧未到，视频层保持隐藏
            _ctx.Vm.GameNavSelection = b;
            window.UpdateLayout();
            Assert.Equal(videoB, _player.PlayedPaths[^1]);
            Assert.Null(_player.Frame);
            Assert.False(b.HasBackgroundVideo);
            AssertSurfaceVisible(window, b, expected: false);

            // 迟到的陈旧帧通知（A 循环停止瞬间的最后一帧）：不得点亮 B 的视频层
            _player.RaiseFrame();
            window.UpdateLayout();
            Assert.False(b.HasBackgroundVideo);
            AssertSurfaceVisible(window, b, expected: false);

            // B 自己的首帧到达：视频层正常点亮
            _player.Frame = NewFrame();
            _player.RaiseFrame();
            window.UpdateLayout();
            Assert.True(b.HasBackgroundVideo);
            AssertSurfaceVisible(window, b, expected: true);

            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>新建 4×4 测试帧位图（测试不释放——Render/播放器仍会访问）。</summary>
    private static WriteableBitmap NewFrame() =>
        new(new Avalonia.PixelSize(4, 4), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormats.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);

    /// <summary>详情页模板里的 FrameSurface 可见性与播放器接线断言；页面已切走（模板卸载）视为隐藏。
    /// 审计修复（2026-09-19）：原静默 return 使播放器注入链断裂时可见性断言整组蒸发（测试照绿）。</summary>
    private static void AssertSurfaceVisible(MainWindow window, GameItemViewModel game, bool expected)
    {
        // 注入链断裂必须立刻显式失败，而非静默跳过整组断言
        Assert.NotNull(game.VideoPlayer);

        var surface = window.GetVisualDescendants()
            .OfType<YetAnotherGameLauncher.Controls.FrameSurface>()
            .FirstOrDefault(s => ReferenceEquals(s.Player, game.VideoPlayer));
        if (surface is null)
        {
            Assert.False(expected); // 页面卸载 = 不可见；期望可见时找不到才是错误
            return;
        }

        Assert.Equal(expected, surface.IsVisible);
        // 回归守卫：FrameSurface 必须保持默认 Stretch 铺满（Left/Top 对齐会 0×0 导致视频不可见）
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Stretch, surface.HorizontalAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Stretch, surface.VerticalAlignment);
    }
}
