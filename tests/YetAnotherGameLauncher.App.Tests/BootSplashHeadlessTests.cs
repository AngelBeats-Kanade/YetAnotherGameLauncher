using Avalonia.Controls;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 启动门控链路回归（UI 线程）：遮蔽下等待首个背景就绪（视频首帧/海报）放行；
/// 资产迟迟不就绪时按超时兜底放行——遮蔽在任何结局下都必须被撤下（finally 兜底）。
/// </summary>
[Collection("sequential")]
public class BootSplashHeadlessTests
{
    [Fact]
    public async Task RunBootGate_ReleasesOnceVideoFirstFrameArrives()
    {
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

            ctx.Vm.BootMinSplash = TimeSpan.FromMilliseconds(60);
            ctx.Vm.BootReadinessTimeout = TimeSpan.FromSeconds(5);
            ctx.Vm.BeginBootSplash();
            var gate = ctx.Vm.RunBootGateAsync();

            // 视频首帧到达（HasBackgroundVideo 翻真）→ 最小时长一过即放行
            await Task.Delay(80);
            Assert.True(ctx.Vm.IsBooting);
            player.Frame = new StubImage();
            player.RaiseFrame();
            Assert.True(ctx.Vm.Games[0].HasBackgroundVideo);

            await gate.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(ctx.Vm.IsBooting);
        }
        finally
        {
            tempDir.Dispose();
        }
    }

    [Fact]
    public async Task RunBootGate_ReleasesOnTimeout_WhenAssetsNeverReady()
    {
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

            // 假播放器永不产帧、视频类背景无海报：永远不就绪，只能靠超时放行
            ctx.Vm.BootMinSplash = TimeSpan.FromSeconds(10);
            ctx.Vm.BootReadinessTimeout = TimeSpan.FromMilliseconds(150);
            ctx.Vm.BeginBootSplash();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ctx.Vm.RunBootGateAsync();

            Assert.False(ctx.Vm.IsBooting);
            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(140));
        }
        finally
        {
            tempDir.Dispose();
        }
    }

    [Fact]
    public async Task RunBootGate_WithoutSplash_IsNoOp()
    {
        // 未开启遮蔽（测试/重复调用）：空跑立即返回，不置任何状态
        using var ctx = VmFactory.Build();
        await ctx.Vm.RunBootGateAsync();
        Assert.False(ctx.Vm.IsBooting);
    }

    [Fact]
    public async Task SplashOverlay_ShowsWhileBooting_AndCollapsesAfterGate()
    {
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
            ctx.Vm.BootMinSplash = TimeSpan.FromMilliseconds(50);
            ctx.Vm.BootReadinessTimeout = TimeSpan.FromMilliseconds(300);

            await HeadlessSession.Instance.Dispatch(() =>
            {
                var window = new Views.MainWindow { DataContext = ctx.Vm, Width = 1120, Height = 720 };
                window.SplashAnimationEnabled = false; // headless 冻结淡出：放行即收起，终态可断言
                window.Show();
                window.UpdateLayout();

                // DataContext 后置开启遮蔽：同步点亮（App 启动路径同序——窗口先建、遮蔽先于上屏开启）
                ctx.Vm.BeginBootSplash();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var splash = window.GetVisualDescendants().OfType<Panel>()
                    .First(p => p.Name == "BootSplash");
                Assert.True(splash.IsVisible);

                // 门控放行（超时路径）：遮蔽随 IsBooting=false 收起
                ctx.Vm.RunBootGateAsync().Wait(TimeSpan.FromSeconds(3));
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.False(ctx.Vm.IsBooting);
                Assert.False(splash.IsVisible);

                window.Close();
            }, CancellationToken.None);
        }
        finally
        {
            tempDir.Dispose();
        }
    }

    /// <summary>非空占位帧：仅驱动 HasBackgroundVideo 翻真，不触平台渲染接口（Bitmap 须在会话线程）。</summary>
    private sealed class StubImage : Avalonia.Media.IImage
    {
        public Avalonia.Size Size => new(4, 4);

        public void Draw(Avalonia.Media.DrawingContext context, Avalonia.Rect sourceRect, Avalonia.Rect destRect)
        {
        }
    }
}
