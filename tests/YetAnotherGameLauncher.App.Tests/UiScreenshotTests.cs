using Avalonia.Controls;
using Avalonia.Headless;
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
/// </summary>
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

            void Capture(string name)
            {
                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(outDir, name), new PngBitmapEncoderOptions());
            }

            // 暗色 · 游戏详情（鸣潮：未安装 + 预下载可用）
            var dark = ctx.Vm.ThemeModes.First(t => t.Mode == ThemeMode.Dark);
            var light = ctx.Vm.ThemeModes.First(t => t.Mode == ThemeMode.Light);
            ctx.Vm.SelectedTheme = dark;
            Capture("01-game-detail-dark.png");

            // 亮色 · 游戏详情
            ctx.Vm.SelectedTheme = light;
            Capture("02-game-detail-light.png");

            // 暗色 · 第二个游戏（终末地）
            ctx.Vm.SelectedTheme = dark;
            ctx.Vm.SelectedGame = ctx.Vm.Games[1];
            Capture("03-game2-dark.png");

            // 暗色 · 设置页
            ctx.Vm.ShowSettingsCommand.Execute(null);
            Capture("04-settings-dark.png");

            window.Close();
            return 0;
        }, CancellationToken.None);
    }
}
