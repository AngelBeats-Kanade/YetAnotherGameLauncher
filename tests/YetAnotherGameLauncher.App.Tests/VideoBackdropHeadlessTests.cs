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
            using var ctx = VmFactory.Build(playerFactory: () => player);
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

            // 初始化期的起播已完成（默认处理器成功）：先全停掉它，制造"死会话"起点——
            // 切页离页走暂停保活（会话存活、重进续播），只有淘汰/关窗/退出才全停；
            // 本用例要测"起播窗口被抢先"，须显式走全停路径
            game.StopVideo();

            // 第一次起播挂在起播窗口内；随后第二次起播（先停旧的、再成功）
            game.SetDetailActive(true);
            Assert.Equal(1, calls);
            game.SetDetailActive(false); // 暂停保活：不再制造死会话
            game.SetDetailActive(true); // 会话存活 → 续播快路径（不计起播次数）
            Assert.Equal(1, calls);
            game.StopVideo(); // 再次全停，进入第二轮起播窗口
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
            using var ctx = VmFactory.Build(playerFactory: () => player);
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
            using var ctx = VmFactory.Build(playerFactory: () => player);
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
/// 首帧到达后视频层可见、切页一律暂停保活（帧与会话保留，游戏页间同样）；
/// 静态图来源不触碰播放器。
/// </summary>
[Collection("sequential")]
public class VideoBackdropHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public VideoBackdropHeadlessTests()
    {
        _ctx = VmFactory.Build();
    }

    public void Dispose() => _ctx.TempDir.Dispose();

    /// <summary>Games[0]（鸣潮）的独占播放器；Games[1] 用 <see cref="SecondPlayer"/>。</summary>
    private VmFactory.FakeVideoPlayer Player => _ctx.Players[0];

    /// <summary>Games[1]（终末地）的独占播放器。</summary>
    private VmFactory.FakeVideoPlayer SecondPlayer => _ctx.Players[1];

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
            Assert.Equal([localVideo], Player.PlayedPaths);

            // 首帧到达：视频层可见（位图随播放器存续，不能 using 释放——Render 仍会访问）
            var frame = new WriteableBitmap(
                new Avalonia.PixelSize(4, 4), new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormats.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
            Player.Frame = frame;
            Player.RaiseFrame();
            window.UpdateLayout();
            Assert.True(game.HasBackgroundVideo);
            AssertSurfaceVisible(window, game, expected: true);

            // 切到设置页：暂停保活——不停止（Stop 数不增）、不清帧、会话与视频层可见标志保留
            //（页面模板已卸载所以渲染面不可见，重进详情页即时恢复）
            var stopsBeforeLeave = Player.StopCount;
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal(stopsBeforeLeave, Player.StopCount);
            Assert.Equal(1, Player.PauseCount);
            Assert.True(Player.IsSessionActive);
            Assert.NotNull(Player.Frame);
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
            Assert.Equal([localVideo], Player.PlayedPaths);

            Player.Frame = NewFrame();
            Player.RaiseFrame();
            window.UpdateLayout();
            Assert.True(game.HasBackgroundVideo);

            // 切到设置页暂停；再切回同一游戏详情：续播快路径——不重新起播（无新 PlayAsync），
            // 不停止，Resume 恰一次；无新帧通知的情况下渲染面重挂即画暂停帧（可见性立即可见）
            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            var playsAfterLeave = Player.PlayedPaths.Count;
            var stopsAfterLeave = Player.StopCount;

            _ctx.Vm.ShowGamesCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal(playsAfterLeave, Player.PlayedPaths.Count);
            Assert.Equal(stopsAfterLeave, Player.StopCount);
            Assert.Equal(1, Player.ResumeCount);
            Assert.True(game.HasBackgroundVideo);
            AssertSurfaceVisible(window, game, expected: true);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task VideoSource_SwitchingGames_KeepsBothAlive_NoRestartOnReturn()
    {
        // 播放器按游戏独占（2026-09-21 起）：游戏 A ↔ 游戏 B 互不干扰各自的会话——
        // 切走暂停保活（不停止、帧保留），B 在自己的播放器上起播，回 A 时凭保留的
        // 帧与会话即时续播（不重新 PlayAsync、无静态图过渡）
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
            Assert.Equal([videoA], Player.PlayedPaths);
            Player.Frame = NewFrame();
            Player.RaiseFrame();
            window.UpdateLayout();
            Assert.True(a.HasBackgroundVideo);

            // 切到游戏 B：A 暂停保活（自己的播放器不停止、帧保留），B 在独占播放器上起播
            var aStops = Player.StopCount;
            _ctx.Vm.GameNavSelection = b;
            window.UpdateLayout();
            Assert.Equal(aStops, Player.StopCount); // A 未被全停
            Assert.Equal(1, Player.PauseCount);
            Assert.NotNull(Player.Frame); // A 的暂停帧保留
            Assert.True(a.HasBackgroundVideo);
            Assert.Equal([videoB], SecondPlayer.PlayedPaths);
            Assert.False(b.HasBackgroundVideo); // B 首帧未到不点亮
            AssertSurfaceVisible(window, b, expected: false);

            // B 自己的首帧到达：正常点亮
            SecondPlayer.Frame = NewFrame();
            SecondPlayer.RaiseFrame();
            window.UpdateLayout();
            Assert.True(b.HasBackgroundVideo);
            AssertSurfaceVisible(window, b, expected: true);

            // 回到 A：续播快路径——不重新起播（路径计数不变）、Resume 恰一次、
            // 无新帧通知即已可见（保留帧当场上屏）
            var aPlays = Player.PlayedPaths.Count;
            var bStops = SecondPlayer.StopCount;
            _ctx.Vm.GameNavSelection = a;
            window.UpdateLayout();
            Assert.Equal(aPlays, Player.PlayedPaths.Count);
            Assert.Equal(1, Player.ResumeCount);
            Assert.Equal(bStops, SecondPlayer.StopCount); // B 也只是暂停保活
            Assert.Equal(1, SecondPlayer.PauseCount);
            Assert.True(a.HasBackgroundVideo);
            AssertSurfaceVisible(window, a, expected: true);

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

            Assert.Empty(Player.PlayedPaths);
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
        // 起播失败 handler 必须在 InitializeAsync 之前就位：用自有实例的工厂注入（本用例只观察 Games[0]）
        var player = new VmFactory.FakeVideoPlayer { PlayHandler = _ => throw new InvalidOperationException("decoder init failed") };
        using var ctx = VmFactory.Build(playerFactory: () => player);
        var localVideo = ctx.TempDir.FilePath("cached", "backdrop.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(localVideo)!);
        await File.WriteAllTextAsync(localVideo, "fake");
        ctx.KuroBackdrop.Resolver = _ => new BackdropSource(localVideo, BackdropKind.Video);

        await ctx.Vm.InitializeAsync();

        var playedPaths = new List<string>();
        var videoLit = true;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = ctx.Vm };
            window.Show();
            window.UpdateLayout();

            ctx.Vm.GameNavSelection = ctx.Vm.Games[0];
            window.UpdateLayout();

            playedPaths.AddRange(player.PlayedPaths);
            videoLit = ctx.Vm.Games[0].HasBackgroundVideo;
            window.Close();
        }, CancellationToken.None);

        // 断言在 Dispatch 外
        Assert.Equal([localVideo], playedPaths); // 起播确实尝试过
        Assert.False(videoLit); // 抛异常后视频层不点亮（海报兜底）
        Assert.Null(player.Frame); // 帧缓冲无残留帧
    }

    [Fact]
    public async Task VideoSource_FailedStart_DetachesFrameNotification()
    {
        // M7 残余收拢（2026-09-19）：起播失败必须退订 FrameUpdated，否则播放器后续产出的帧
        // 会点亮本页视频层。探针：失败后注入非空帧再手动触发通知——已退订则 HasBackgroundVideo
        // 恒 false；悬挂订阅会把它点亮（M7 的旧论证里 Frame 恒 null，行为无差异、探测不到悬挂
        // 订阅，这里注入帧才让泄漏可见）。起播失败 handler 须在 InitializeAsync 前就位：自有实例工厂注入。
        var player = new VmFactory.FakeVideoPlayer { PlayHandler = _ => throw new InvalidOperationException("decoder init failed") };
        using var ctx = VmFactory.Build(playerFactory: () => player);
        var localVideo = ctx.TempDir.FilePath("cached", "backdrop.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(localVideo)!);
        await File.WriteAllTextAsync(localVideo, "fake");
        ctx.KuroBackdrop.Resolver = _ => new BackdropSource(localVideo, BackdropKind.Video);

        await ctx.Vm.InitializeAsync();

        var videoLit = true;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = ctx.Vm };
            window.Show();
            window.UpdateLayout();

            ctx.Vm.GameNavSelection = ctx.Vm.Games[0];
            window.UpdateLayout(); // 起播同步失败（桩抛异常在首个 await 前）：finally 已退订

            var frame = new WriteableBitmap(
                new Avalonia.PixelSize(4, 4), new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormats.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
            player.Frame = frame;
            player.RaiseFrame(); // 迟到通知：悬挂订阅会在此点亮视频层
            videoLit = ctx.Vm.Games[0].HasBackgroundVideo;
            window.Close();
        }, CancellationToken.None);

        Assert.False(videoLit); // 退订缺失即红
    }

    [Fact]
    public async Task VideoSource_SwitchingGames_NoCrossGameLighting_PerPlayerSessions()
    {
        // 播放器按游戏独占后的防串台契约：A 的帧通知只可能影响 A 的视频层——A 暂停保活期间
        // 它的迟到/残留通知（含帧缓冲非空为证的通知）不得点亮 B 的页面；B 只被自己的首帧点亮。
        // （旧共享单例时代的"停止即清帧"防线保留在 StopVideo 契约里：全停后迟到的陈旧通知
        //  以空帧缓冲为证不再点亮——由 FailedStart/PlayAsyncThrows 用例覆盖退订路径）
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
            Assert.Equal([videoA], Player.PlayedPaths);
            Player.Frame = NewFrame();
            Player.RaiseFrame();
            window.UpdateLayout();
            Assert.True(a.HasBackgroundVideo);

            // 切到 B：A 暂停保活（帧保留）；B 在独占播放器上起播，首帧未到不点亮
            _ctx.Vm.GameNavSelection = b;
            window.UpdateLayout();
            Assert.Equal([videoB], SecondPlayer.PlayedPaths);
            Assert.Null(SecondPlayer.Frame);
            Assert.False(b.HasBackgroundVideo);
            AssertSurfaceVisible(window, b, expected: false);

            // A 的帧通知（暂停会话的迟到投递，帧缓冲非空）：只影响 A 的标志，不点亮 B
            Player.RaiseFrame();
            window.UpdateLayout();
            Assert.True(a.HasBackgroundVideo);
            Assert.False(b.HasBackgroundVideo);
            AssertSurfaceVisible(window, b, expected: false);

            // B 自己的首帧到达：视频层正常点亮
            SecondPlayer.Frame = NewFrame();
            SecondPlayer.RaiseFrame();
            window.UpdateLayout();
            Assert.True(b.HasBackgroundVideo);
            AssertSurfaceVisible(window, b, expected: true);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task VideoSource_ParkedBeyondLimit_OldestSessionEvicted()
    {
        // 保活上限：2 路暂停 + 1 路在播。第 4 个游戏播放时最旧的暂停会话被全停清帧淘汰；
        // 被淘汰的游戏重进时凭已解析路径重新起播（自愈）
        var videos = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var video = _ctx.TempDir.FilePath("cached", $"g{i}.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(video)!);
            await File.WriteAllTextAsync(video, "fake");
            videos.Add(video);
        }

        var configJson = """
            {
              "settings": { "installRoot": "~/yagl-test-games", "theme": "Dark" },
              "games": [
                { "id": "g0", "displayName": "G0", "channel": "kuro", "installDir": "G0", "executable": "a.exe",
                  "servers": [ { "id": "cn", "name": "国服" } ] },
                { "id": "g1", "displayName": "G1", "channel": "kuro", "installDir": "G1", "executable": "a.exe",
                  "servers": [ { "id": "cn", "name": "国服" } ] },
                { "id": "g2", "displayName": "G2", "channel": "hypergryph", "installDir": "G2", "executable": "a.exe",
                  "servers": [ { "id": "global", "name": "国际服" } ] },
                { "id": "g3", "displayName": "G3", "channel": "hypergryph", "installDir": "G3", "executable": "a.exe",
                  "servers": [ { "id": "global", "name": "国际服" } ] }
              ]
            }
            """;
        // 4 游戏目录需要独立 Context（默认样例只有 2 个游戏）
        using var ctx = VmFactory.Build(configJson: configJson);
        ctx.KuroBackdrop.Resolver = _ => new BackdropSource(videos[0], BackdropKind.Video);
        ctx.GryphlineBackdrop.Resolver = _ => new BackdropSource(videos[2], BackdropKind.Video);
        await ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = ctx.Vm };
            window.Show();
            window.UpdateLayout();

            // 播放顺序 g0 → g1 → g2 → g3：g3 播放时暂停队列 [g0,g1,g2] 超限 → g0 被淘汰
            for (var i = 1; i < 4; i++)
            {
                ctx.Vm.GameNavSelection = ctx.Vm.Games[i];
                window.UpdateLayout();
            }

            var players = ctx.Players;
            // 各自起播各一次（StartVideoAsync 开场的 StopVideo 属正常起播序列）
            Assert.Equal([videos[0]], players[0].PlayedPaths);
            Assert.True(players[1].StopCount >= 1); // g1 在暂停队列里，未被淘汰
            Assert.True(players[2].StopCount >= 1); // g2 同上
            // g0 被淘汰：起播序列之外多了一次全停（帧清空、会话终结）
            Assert.Null(players[0].Frame);
            Assert.False(players[0].IsSessionActive);
            Assert.False(ctx.Vm.Games[0].HasBackgroundVideo);

            // 淘汰后重进 g0：凭已解析路径重新起播（自愈）
            ctx.Vm.GameNavSelection = ctx.Vm.Games[0];
            window.UpdateLayout();
            Assert.Equal(videos[0], players[0].PlayedPaths[^1]);
            Assert.Equal(2, players[0].PlayedPaths.Count);

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
