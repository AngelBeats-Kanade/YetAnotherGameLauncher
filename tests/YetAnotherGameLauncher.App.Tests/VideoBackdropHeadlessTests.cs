using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;
using Xunit;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 背景视频链路无头回归：解析器返回视频源时，进入详情页起播（fake 播放器）、
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

            // 首帧到达：视频层可见
            using var frame = new WriteableBitmap(
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

    /// <summary>详情页模板里的 FrameSurface 可见性与播放器接线断言；页面已切走（模板卸载）视为隐藏。</summary>
    private static void AssertSurfaceVisible(MainWindow window, GameItemViewModel game, bool expected)
    {
        if (game.VideoPlayer is null)
        {
            return; // 无播放器注入的构建：XAML 层恒隐藏，跳过
        }

        var surface = window.GetVisualDescendants()
            .OfType<YetAnotherGameLauncher.Controls.FrameSurface>()
            .FirstOrDefault(s => ReferenceEquals(s.Player, game.VideoPlayer));
        if (surface is null)
        {
            Assert.False(expected); // 页面卸载 = 不可见；期望可见时找不到才是错误
            return;
        }

        Assert.Equal(expected, surface.IsVisible);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, surface.HorizontalAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, surface.VerticalAlignment);
    }
}
