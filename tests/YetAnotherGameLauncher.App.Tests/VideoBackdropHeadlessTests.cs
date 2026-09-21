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

            // 初始化期的起播已完成（默认处理器成功）：先离页停掉它，制造"死会话"起点——
            // 新契约下 SetDetailActive(true) 对存活会话走续播快路径，不再重复起播
            game.SetDetailActive(false);

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

    [Fact]
    public async Task StartVideo_DefersPastTransferWindow_AndDropsSupersededCalls()
    {
        // 2026-09-21 真机插桩实锤：切游戏时新背景视频的首帧位图分配/上传是 UI 线程重活
        // （大视频 Debug 单帧渲染可达 80ms），落在指示点迁移编舞窗口（420ms）内会把动画
        // 饿成"两段缩放、中间无过渡"。起播必须延迟到编舞之后；延迟窗口内被更新的
        // 切换抢先时，旧调用直接放弃（不 PlayAsync、不干扰新一代的状态）。
        var tempDir = new TestSupport.TempDir();
        try
        {
            var player = new VmFactory.FakeVideoPlayer();
            using var ctx = VmFactory.Build(videoPlayer: player);
            GameItemViewModel.VideoStartDeferral = TimeSpan.FromMilliseconds(150);
            var localVideo = tempDir.FilePath("cached", "backdrop.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(localVideo)!);
            await File.WriteAllTextAsync(localVideo, "fake");
            ctx.KuroBackdrop.Resolver = _ => new BackdropSource(localVideo, BackdropKind.Video);
            await ctx.Vm.InitializeAsync();
            var game = ctx.Vm.Games[0];

            // 延迟窗口内不起播
            game.SetDetailActive(true);
            await Task.Delay(60);
            Assert.Empty(player.PlayedPaths);

            // 延迟窗口内二次触发（离开再进入）：第一次的挂起调用被抢先，最终只起播一次
            game.SetDetailActive(false);
            game.SetDetailActive(true);
            await Task.Delay(250);
            Assert.Equal([localVideo], player.PlayedPaths);
        }
        finally
        {
            GameItemViewModel.VideoStartDeferral = TimeSpan.Zero;
            tempDir.Dispose();
        }
    }

    [Fact]
    public async Task StartVideo_SuspendedDuringDeferralWindow_DoesNotStartWhileHidden()
    {
        // 起播延迟窗口内切去非游戏页（SuspendVideo）：挂起的起播必须放弃——不得在页外
        // 隐形起播浪费解码（重进详情页时经续播快路径或路径重启恢复）。
        var tempDir = new TestSupport.TempDir();
        try
        {
            var player = new VmFactory.FakeVideoPlayer();
            using var ctx = VmFactory.Build(videoPlayer: player);
            GameItemViewModel.VideoStartDeferral = TimeSpan.FromMilliseconds(150);
            var localVideo = tempDir.FilePath("cached", "backdrop.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(localVideo)!);
            await File.WriteAllTextAsync(localVideo, "fake");
            ctx.KuroBackdrop.Resolver = _ => new BackdropSource(localVideo, BackdropKind.Video);
            await ctx.Vm.InitializeAsync();
            var game = ctx.Vm.Games[0];

            // 延迟窗口内暂停（模拟切去设置页）：挂起的起播过期放弃
            await Task.Delay(60);
            game.SuspendVideo();
            await Task.Delay(250);
            Assert.Empty(player.PlayedPaths);

            // 重进详情页：凭已解析路径正常起播（延迟窗口 150ms，等待须覆盖）
            game.SetDetailActive(true);
            await Task.Delay(300);
            Assert.Equal([localVideo], player.PlayedPaths);
        }
        finally
        {
            GameItemViewModel.VideoStartDeferral = TimeSpan.Zero;
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
/// 背景视频链路无头回归：解析器返回视频源时，进入详情页起播（fake 播放器）、
/// 首帧到达后视频层可见、切去非游戏页暂停保活（帧与会话保留）、切另一游戏页全停清帧；
/// 静态图来源不触碰播放器。
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
    public async Task VideoSource_PlaysWhenDetailVisible_PausesWhenLeavingToOtherPage()
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

            // 切到设置页：暂停保活——不停止（Stop 数不增）、不清帧、会话与视频层可见标志保留
            //（页面模板已卸载所以渲染面不可见，重进详情页即时恢复）
            var stopsBeforeLeave = _player.StopCount;
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal(stopsBeforeLeave, _player.StopCount);
            Assert.Equal(1, _player.PauseCount);
            Assert.True(_player.IsSessionActive);
            Assert.NotNull(_player.Frame);
            Assert.True(game.HasBackgroundVideo);
            AssertSurfaceVisible(window, game, expected: false);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task VideoSource_ReentersDetailPage_ResumesWithoutRestarting()
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

            _player.Frame = NewFrame();
            _player.RaiseFrame();
            window.UpdateLayout();
            Assert.True(game.HasBackgroundVideo);

            // 切到设置页暂停；再切回同一游戏详情：续播快路径——不重新起播（无新 PlayAsync），
            // 不停止，Resume 恰一次；无新帧通知的情况下渲染面重挂即画暂停帧（可见性立即可见）
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            var playsAfterLeave = _player.PlayedPaths.Count;
            var stopsAfterLeave = _player.StopCount;

            _ctx.Vm.ShowGamesCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal(playsAfterLeave, _player.PlayedPaths.Count);
            Assert.Equal(stopsAfterLeave, _player.StopCount);
            Assert.Equal(1, _player.ResumeCount);
            Assert.True(game.HasBackgroundVideo);
            AssertSurfaceVisible(window, game, expected: true);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task VideoSource_SuspendedGame_SwitchingToAnotherGame_StopsAndClearsFrame()
    {
        // 保活不弱化切游戏契约：从"暂停保活中的游戏 A"直接切到游戏 B（经侧栏或返回路径），
        // A 的会话必须全停清帧——否则共享播放器上 B 的会话会被 A 的悬挂订阅/残留帧污染
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

            _ctx.Vm.GameNavSelection = a;
            window.UpdateLayout();
            _player.Frame = NewFrame();
            _player.RaiseFrame();
            window.UpdateLayout();
            Assert.True(a.HasBackgroundVideo);

            // 切到设置页（A 暂停保活），再直接切到游戏 B：A 全停
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal(1, _player.PauseCount);

            _ctx.Vm.GameNavSelection = b;
            window.UpdateLayout();
            Assert.True(_player.StopCount >= 1);
            Assert.Null(_player.Frame); // 停止即清帧：B 未出首帧前不被 A 的残留帧点亮
            Assert.Equal(videoB, _player.PlayedPaths[^1]);
            Assert.False(b.HasBackgroundVideo);
            AssertSurfaceVisible(window, b, expected: false);

            // A 的迟到的陈旧帧通知不得点亮 B（暂停中被停掉的会话同样受契约保护）
            _player.RaiseFrame();
            window.UpdateLayout();
            Assert.False(b.HasBackgroundVideo);

            // B 自己的首帧到达：正常点亮
            _player.Frame = NewFrame();
            _player.RaiseFrame();
            window.UpdateLayout();
            Assert.True(b.HasBackgroundVideo);
            AssertSurfaceVisible(window, b, expected: true);

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
