using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;

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

            void BringCardIntoView(Window host, string borderName)
            {
                // 设置页内容超出一屏：把目标卡滚进视口再截图（ BringIntoView 驱动祖先 ScrollViewer）
                var card = host.GetVisualDescendants()
                    .OfType<Border>()
                    .FirstOrDefault(b => b.Name == borderName);
                card?.BringIntoView();
                host.UpdateLayout();
            }

            // 模拟鸣潮背景图（resolver 已 stub，直接给本地测试图文件）
            var bgPath = ctx.TempDir.FilePath("kr_game_cache", "animate_bg", "h1", "home_1.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(bgPath)!);
            CreateTestBackground(bgPath);
            ctx.KuroBackdrop.Resolver = _ => new BackdropSource(bgPath, BackdropKind.Image);
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

            // 暗色 · 第二个游戏（终末地，官方远程背景图，验证 URL 加载链路；
            // 并构造"检测到游戏文件"态：有主程序但未登记版本 → 状态胶囊提示 + 登记版本按钮）
            ctx.Vm.SelectedTheme = dark;
            ctx.Vm.Games[1].Game.Icon = endfieldIconUrl;
            var endfieldExe = Path.Combine(ctx.Vm.Games[1].InstallDirPath, "Endfield.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(endfieldExe)!);
            await File.WriteAllBytesAsync(endfieldExe, "MZ"u8.ToArray());
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

            // 暗色 · 设置页（外观卡：主题 + 语言；滚动到网络代理卡展示单选组）
            ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            BringCardIntoView(window, "ProxyCard");
            Capture("05-settings-dark.png");

            // 暗色 · 关于页
            ctx.Vm.ShowAboutCommand.Execute(null);
            window.UpdateLayout();
            Capture("06-about-dark.png");

            // 英文 · 鸣潮详情页（i18n 热切换：UI 文案与游戏名变英文，背景仍为本地帧探测图）
            // 先清掉前面步骤残留的 toast（4s TTL 内会活到这里，中英同屏干扰 i18n 审查——judge 实锤）
            ctx.Vm.Toasts.Clear();
            ctx.Vm.SelectedGame = ctx.Vm.Games[0];
            ctx.Vm.ShowGamesCommand.Execute(null);
            window.UpdateLayout();
            ctx.Vm.Loc.SetLanguage("en-US");
            window.UpdateLayout();
            Capture("07-game-detail-en.png");

            // 回中文 · 暗色 · 已安装的鸣潮：抽卡图标入口 + 预下载提示（琥珀点/状态行/描边按钮）
            ctx.Vm.Loc.SetLanguage("zh-CN");
            var wuwaZip = TestZip.Create(("Client/game.exe", "MZ"));
            ctx.Kuro.Manifests["3.6.0"] = new GameManifest
            {
                Version = "3.6.0",
                Files =
                [
                    new ManifestFile("Client/game.exe", wuwaZip.Length, Hashing.Md5Hex(wuwaZip), Url: "https://cdn/wuwa-full.zip"),
                    new ManifestFile("Client/Binaries/Win64/Client-Win64-Shipping.exe", wuwaZip.Length, Hashing.Md5Hex(wuwaZip), Url: "https://cdn/wuwa-full.zip"),
                ],
            };
            ctx.Downloader.Responses["https://cdn/wuwa-full.zip"] = wuwaZip;
            await ctx.Vm.Games[0].InstallOrUpdateCommand.ExecuteAsync(null);
            window.UpdateLayout();
            Capture("08-game-detail-installed-dark.png");

            // 暗色 · 最大化视觉状态（仅窗口外缘顶角去圆角 + 标题栏还原图标；
            // 内容卡左上圆角是内部角，最大化保留）。
            // headless 平台不追踪 WindowState，直接驱动真机窗口状态变化所回写的同一 VM 绑定链
            ctx.Vm.IsWindowMaximized = true;
            window.UpdateLayout();
            Capture("13-game-detail-maximized-dark.png");
            ctx.Vm.IsWindowMaximized = false;
            window.UpdateLayout();

            // 暗色 · 终末地校验修复确认条（包式渠道：重下整包前需确认）
            var efZip = TestZip.Create(("bin/ef.exe", "MZ"));
            ctx.Gryphline.Manifests["1.2.0"] = new GameManifest
            {
                Version = "1.2.0",
                EntriesAreArchives = true,
                Files = [new ManifestFile("ef-full.zip", efZip.Length, Hashing.Md5Hex(efZip), Url: "https://cdn/ef-full.zip")],
            };
            ctx.Downloader.Responses["https://cdn/ef-full.zip"] = efZip;
            ctx.Vm.SelectedGame = ctx.Vm.Games[1];
            await ctx.Vm.Games[1].RefreshAsync();
            await ctx.Vm.Games[1].InstallOrUpdateCommand.ExecuteAsync(null); // 安装
            await ctx.Vm.Games[1].InstallOrUpdateCommand.ExecuteAsync(null); // 校验 → 确认条
            window.UpdateLayout();
            Capture("09-repair-confirm-dark.png");

            window.Close();
            return 0;
        }, CancellationToken.None);
    }

    /// <summary>
    /// 启动失败覆盖层 + 启动设置卡（Linux：umu 启动二选一 + Proton 发行版下拉 + 组件状态卡）的视觉自检
    /// （Linux 平台语义；VmFactory 未注入原生 umu 启动器/组件准备器 → 走通用预检失败出覆盖层、
    /// 准备按钮禁用）。
    /// </summary>
    [Fact]
    public async Task Export_LaunchErrorOverlay_ForReview()
    {
        var outDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "ui-review"));
        Directory.CreateDirectory(outDir);

        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: []);
        ctx.Gryphline.VersionInfo = new ChannelVersionInfo { LatestVersion = "1.2.0" };

        await HeadlessSession.Instance.Dispatch(async () =>
        {
            await ctx.Vm.InitializeAsync();
            var window = new MainWindow { DataContext = ctx.Vm, Width = 1120, Height = 720 };
            window.NavIndicatorAnimationEnabled = false;
            window.Show();
            window.UpdateLayout();

            void Capture(string name)
            {
                Thread.Sleep(150);
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(400);
                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(outDir, name), new PngBitmapEncoderOptions());
            }

            var wuwa = ctx.Vm.Games[0];

            // 空态（官方未投放背景 + 无缓存）：渐变 + 官方图标水印
            var iconFile = ctx.TempDir.FilePath("game-icon.png");
            CreateTestBackground(iconFile);
            const string wuwaIconUrl = "https://is1-ssl.mzstatic.com/wuwa-icon.jpg";
            ctx.BackgroundHandler.Map(wuwaIconUrl, await File.ReadAllBytesAsync(iconFile));
            wuwa.Game.Icon = wuwaIconUrl;
            await wuwa.RefreshAsync();
            Capture("12-detail-empty-state-dark.png");

            var exePath = Path.Combine(
                wuwa.InstallDirPath, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            await File.WriteAllBytesAsync(exePath, "MZ"u8.ToArray());
            await wuwa.RefreshAsync();

            // 主程序就位 + umu 模板 + umu 未装 → 启动预检失败 → 错误覆盖层（含一键安装按钮）
            await wuwa.LaunchCommand.ExecuteAsync(null);
            Assert.True(wuwa.HasLaunchError, $"launchError=null, status={wuwa.StatusText}");
            window.UpdateLayout();
            Capture("10-launch-error-overlay-dark.png");

            // 启动设置页：Linux 启动卡（umu 启动 + Proton 发行版下拉 + 组件状态卡），滚动到完整可见
            ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();
            var launchCard = window.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(b => b.Name == "LaunchCard");
            launchCard?.BringIntoView();
            window.UpdateLayout();
            Capture("11-launch-settings-linux-dark.png");

            // 轻提示：两条不同种类的 toast 叠在右上（状态变化时由 VM 自动弹出，此处手动注入）
            ctx.Vm.ShowToast("鸣潮", "检测到游戏文件，可直接启动", ToastKind.Success);
            ctx.Vm.ShowToast("鸣潮", "可预下载新版本，本地 3.6.0", ToastKind.Warning);
            window.UpdateLayout();
            Capture("14-toast-dark.png");

            window.Close();
            return 0;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Proton 更新确认覆盖层的视觉自检：Linux 设置页选 GE-Proton → 检测到新版本 →
    /// 纱罩 + 居中卡（新版本号 + 旧版本清理提示 + 更新/取消）。
    /// </summary>
    [Fact]
    public async Task Export_ProtonUpdateConfirm_ForReview()
    {
        var outDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "ui-review"));
        Directory.CreateDirectory(outDir);

        using var ctx = VmFactory.Build(
            templateFactory: () => VmFactory.SampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: [],
            umuProvisioner: new UpdateConfirmProvisioner
            {
                InstalledProtonPath = "/compat/GE-Proton11-6",
                LatestTag = "GE-Proton11-7",
            });

        await HeadlessSession.Instance.Dispatch(async () =>
        {
            await ctx.Vm.InitializeAsync();
            var window = new MainWindow { DataContext = ctx.Vm, Width = 1120, Height = 720 };
            window.NavIndicatorAnimationEnabled = false;
            window.Show();
            window.UpdateLayout();

            ctx.Vm.SelectedGame = ctx.Vm.Games[0];
            ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var settings = (ctx.Vm.CurrentPage as GameSettingsViewModel)?.LaunchSettings;
            Assert.NotNull(settings);
            settings.SelectedProtonFlavor = "GE-Proton";

            // 纱罩回归守卫：覆盖层弹出后，页面背景点必须比弹出前显著变暗
            // （2026-09 实锤：视觉分析工具曾连续误报"纱罩未生效"，以像素级断言为准）
            int LuminanceAt(int x, int y)
            {
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(400);
                using var probe = window.CaptureRenderedFrame();
                Assert.NotNull(probe);
                using var fb = probe.Lock();
                var addr = fb.Address + (y * fb.RowBytes) + (x * 4);
                return System.Runtime.InteropServices.Marshal.ReadByte(addr)
                    + System.Runtime.InteropServices.Marshal.ReadByte(addr + 1)
                    + System.Runtime.InteropServices.Marshal.ReadByte(addr + 2);
            }

            var before = LuminanceAt(430, 130);
            await settings.CheckProtonUpdateCommand.ExecuteAsync(null);
            Assert.True(settings.ShowProtonUpdateConfirm);
            var after = LuminanceAt(430, 130);
            Assert.True(after < before, $"纱罩未压暗底页：before={before} after={after}");

            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Thread.Sleep(150);
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(400);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(outDir, "15-proton-update-confirm-dark.png"), new PngBitmapEncoderOptions());

            window.Close();
            return 0;
        }, CancellationToken.None);
    }

    /// <summary>更新确认截图的组件准备器替身：本地 11-6、上游 11-7 → 必然弹更新确认。</summary>
    private sealed class UpdateConfirmProvisioner : YetAnotherGameLauncher.Core.Abstractions.IUmuComponentProvisioner
    {
        public string? InstalledProtonPath { get; set; }

        public string LatestTag { get; set; } = "GE-Proton11-7";

        public bool IsProtonReady(string protonPath) => true;

        public bool IsRuntimeReady(string runtimeVariant) => true;

        public (string Variant, string Name)? ResolveRequiredRuntime(string protonRequest) => null;

        public string? FindInstalledProton(string protonRequest) => InstalledProtonPath;

        public Task<string> FetchLatestProtonTagAsync(
            string protonRequest, CancellationToken cancellationToken = default) => Task.FromResult(LatestTag);

        public Task<string> UpdateProtonAsync(
            string protonRequest, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(InstalledProtonPath ?? "/compat/GE-Proton11-7");

        public Task<string> EnsureProtonAsync(
            string protonRequest, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(InstalledProtonPath ?? "/compat/GE-Proton11-7");

        public Task EnsureRuntimeAsync(
            string runtimeVariant, string runtimeName,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
