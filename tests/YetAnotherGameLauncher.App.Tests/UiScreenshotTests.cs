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

        // headless 内 HttpClient 走 Stub：把官方背景图字节注册进去，验证远程加载链路
        var endfieldBgUrl = "https://web.hycdn.cn/upload/image/20260411/dde7c30f64cb985113539ec6c7a03c38.jpg";
        var endfieldBg = await new HttpClient().GetByteArrayAsync(endfieldBgUrl);
        ctx.BackgroundHandler.Map(endfieldBgUrl, endfieldBg);
        var endfieldIconUrl = "https://is1-ssl.mzstatic.com/image/thumb/Purple211/v4/dd/af/42/ddaf42c8-5bea-adf0-e6e7-b67f291868aa/AppIcon-0-0-1x_U007emarketing-0-8-0-85-220.png/512x512bb.jpg";
        ctx.BackgroundHandler.Map(endfieldIconUrl, await new HttpClient().GetByteArrayAsync(endfieldIconUrl));
        // 终末地背景走背景解析器（模拟 get_main_bg_image 返回的当期直链）
        ctx.GryphlineBackdrop.Resolver = _ => new BackdropSource(endfieldBgUrl, BackdropKind.Image);

        await HeadlessSession.Instance.Dispatch(async () =>
        {
            await ctx.Vm.InitializeAsync();
            var window = new MainWindow { DataContext = ctx.Vm, Width = 1120, Height = 720 };
            // headless 中迁移动画冻结在首帧会遮盖基值：截图需要指示点的终态位置
            window.NavIndicatorAnimationEnabled = false;
            window.Show();
            window.UpdateLayout();

            void WaitForBackdrop()
            {
                // 背景为装饰性异步加载：轮询等待其就绪（上限 5s），避免截到纯渐变帧
                for (var i = 0; i < 100 && !ctx.Vm.Games[0].HasBackgroundImage; i++)
                {
                    Thread.Sleep(50);
                }
            }

            void Capture(string name)
            {
                Thread.Sleep(150); // headless 动画时钟靠手动 tick 推进：先等真实时钟，再显式推进
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(400);
                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(outDir, name), new PngBitmapEncoderOptions());
            }

            // 模拟库洛官方启动器背景帧缓存：解析器走与 KuroBackdropResolver 相同的本地帧探测
            var bgPath = ctx.TempDir.FilePath("kr_game_cache", "animate_bg", "h1", "home_1.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(bgPath)!);
            CreateTestBackground(bgPath);
            ctx.KuroBackdrop.Resolver = _ => new BackdropSource(
                YetAnotherGameLauncher.Core.Services.KuroLauncherBackground
                    .FindLatestFrame(ctx.Vm.Games[0].InstallDirPath)!, BackdropKind.Image);
            // headless 测试进程解析不了 app 的 avares:// 资源：图标走 stub http
            const string wuwaIconUrl = "https://is1-ssl.mzstatic.com/wuwa-icon.jpg";
            ctx.BackgroundHandler.Map(wuwaIconUrl, await new HttpClient().GetByteArrayAsync(endfieldIconUrl));
            ctx.Vm.Games[0].Game.Icon = wuwaIconUrl;
            await ctx.Vm.Games[0].RefreshAsync();

            // 暗色 · 游戏详情（鸣潮：未安装 + 预下载可用 + 背景图）
            var dark = ctx.Vm.ThemeModes.First(t => t.Mode == ThemeMode.Dark);
            var light = ctx.Vm.ThemeModes.First(t => t.Mode == ThemeMode.Light);
            ctx.Vm.SelectedTheme = dark;
            window.UpdateLayout();
            WaitForBackdrop();
            Capture("01-game-detail-dark.png");

            // 亮色 · 游戏详情
            ctx.Vm.SelectedTheme = light;
            window.UpdateLayout();
            Capture("02-game-detail-light.png");

            // 暗色 · 游戏设置页（位置 / 启动方式 / 启动参数）
            ctx.Vm.SelectedTheme = dark;
            ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();
            Capture("02b-game-settings-dark.png");
            ctx.Vm.ShowGamesCommand.Execute(null);

            // 暗色 · 第二个游戏（终末地，官方远程背景图，验证 URL 加载链路）
            ctx.Vm.SelectedTheme = dark;
            ctx.Vm.Games[1].Game.Icon = endfieldIconUrl;
            ctx.Vm.SelectedGame = ctx.Vm.Games[1];
            await ctx.Vm.Games[1].RefreshAsync();
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

            // 英文 · 鸣潮详情页（i18n 热切换：UI 文案与游戏名变英文，背景仍为本地帧探测图）
            ctx.Vm.SelectedGame = ctx.Vm.Games[0];
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
