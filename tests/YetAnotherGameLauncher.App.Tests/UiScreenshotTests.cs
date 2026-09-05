using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Themes;
using YetAnotherGameLauncher.Views;
using Xunit;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 视觉自检工具（见 .zcode/skills/avalonia-ui-review）：把真实窗口渲染成 PNG
/// 供人工/代理检查。产物在仓库根 artifacts/ui-review/（已 gitignore）。
/// 注意：换页后的模板构建发生在下一轮布局，切页后需 window.UpdateLayout()。
/// </summary>
[Collection("sequential")]
public class UiScreenshotTests
{
    [Fact]
    public async Task Export_UiScreenshots_ForReview()
    {
        var outDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "ui-review"));
        Directory.CreateDirectory(outDir);

        using var ctx = VmFactory.Build();
        ctx.Kuro.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "3.6.0",
            PredownloadAvailable = true,
            PredownloadVersion = "3.7.0",
        };
        ctx.Gryphline.VersionInfo = new ChannelVersionInfo { LatestVersion = "1.2.0" };

        await HeadlessSession.Instance.Dispatch(async () =>
        {
            await ctx.Vm.InitializeAsync();
            var window = new MainWindow { DataContext = ctx.Vm, Width = 1120, Height = 720 };
            window.Show();
            window.UpdateLayout();

            void Capture(string name)
            {
                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(outDir, name), new PngBitmapEncoderOptions());
            }

            // 给鸣潮挂一张渐变测试背景图，验证详情页背景 + 模糊 + 遮罩效果
            var bgPath = ctx.TempDir.FilePath("bg.png");
            CreateTestBackground(bgPath);
            ctx.Vm.Games[0].Game.BackgroundImage = bgPath;
            await ctx.Vm.Games[0].RefreshAsync();

            // 暗色 · 游戏详情（鸣潮：未安装 + 预下载可用 + 背景图）
            var dark = ctx.Vm.ThemeModes.First(t => t.Mode == ThemeMode.Dark);
            var light = ctx.Vm.ThemeModes.First(t => t.Mode == ThemeMode.Light);
            ctx.Vm.SelectedTheme = dark;
            window.UpdateLayout();
            Capture("01-game-detail-dark.png");

            // 亮色 · 游戏详情
            ctx.Vm.SelectedTheme = light;
            window.UpdateLayout();
            Capture("02-game-detail-light.png");

            // 暗色 · 启动设置展开
            ctx.Vm.SelectedTheme = dark;
            window.UpdateLayout();
            var launchToggle = window.GetVisualDescendants()
                .OfType<ToggleButton>().First(t => t.Classes.Contains("card-toggle"));
            launchToggle.IsChecked = true;
            window.UpdateLayout();
            Capture("02b-launch-settings-dark.png");
            launchToggle.IsChecked = false;

            // 暗色 · 第二个游戏（终末地，无背景图）
            ctx.Vm.SelectedTheme = dark;
            ctx.Vm.SelectedGame = ctx.Vm.Games[1];
            window.UpdateLayout();
            Capture("03-game2-dark.png");

            // 暗色 · 侧栏收起
            ctx.Vm.ToggleSidebarCommand.Execute(null);
            window.UpdateLayout();
            Capture("04-sidebar-collapsed-dark.png");
            ctx.Vm.ToggleSidebarCommand.Execute(null);
            window.UpdateLayout();

            // 暗色 · 设置页（外观卡：主题 + 语言）
            ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            Capture("05-settings-dark.png");

            // 暗色 · 关于页
            ctx.Vm.ShowAboutCommand.Execute(null);
            window.UpdateLayout();
            Capture("06-about-dark.png");

            // 英文 · 详情页（i18n 热切换）
            ctx.Vm.ShowGamesCommand.Execute(null);
            window.UpdateLayout();
            ctx.Vm.Loc.SetLanguage("en-US");
            window.UpdateLayout();
            Capture("07-game-detail-en.png");

            window.Close();
            return 0;
        }, CancellationToken.None);
    }

    /// <summary>画一张蓝色系渐变测试图，供详情页背景（模糊）截图使用。</summary>
    private static void CreateTestBackground(string path)
    {
        using var rtb = new RenderTargetBitmap(new PixelSize(960, 640));
        using (var dc = rtb.CreateDrawingContext())
        {
            dc.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x2E, 0x7C, 0xF6), 0),
                    new GradientStop(Color.FromRgb(0x0E, 0x1B, 0x33), 1),
                },
            }, new Rect(0, 0, 960, 640));
        }

        rtb.Save(path, new PngBitmapEncoderOptions());
    }
}
