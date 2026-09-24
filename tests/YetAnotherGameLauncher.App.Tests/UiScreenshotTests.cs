using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
/// 审计修复（2026-09-19）：原实现把 await/断言写进 Dispatch(async ...) lambda——
/// 断言失败被吞（假绿），且真实外网 CDN 拉取（离线必挂）。现形态：
/// await Dispatch(同步 lambda)（lambda 在会话线程跑完才返回、异常传播）；
/// 异步步骤用 RunJobs 泵到完成（RunToCompletion）；"远程"字节全部本地生成后注册进桩；
/// 帧在 lambda 内编码为 PNG 字节，落盘与非空断言一律在 Dispatch 之外。
/// </summary>
[Collection("sequential")]
public class UiScreenshotTests
{
    /// <summary>在会话线程上启动任务并同步等待完成（RunJobs 泵；30s 上限防挂死）。</summary>
    private static void RunToCompletion(Func<Task> call)
    {
        var task = call();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        Assert.True(task.IsCompleted, "异步步骤 30s 内未完成（RunJobs 泵停摆）");
        task.GetAwaiter().GetResult();
    }

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

        // "远程"背景/图标字节本地生成（审计修复：不再真实拉取外网 CDN；URL 仍走桩验证加载链路）。
        // 注意：渲染字节必须在会话线程生成（RenderTargetBitmap 依赖渲染接口），故移入 Dispatch 内。
        var endfieldBgUrl = "https://web.hycdn.cn/upload/image/20260411/dde7c30f64cb985113539ec6c7a03c38.jpg";
        var endfieldIconUrl = "https://is1-ssl.mzstatic.com/endfield-icon.jpg";
        // 终末地背景走背景解析器（模拟 get_main_bg_image 返回的当期直链）
        ctx.GryphlineBackdrop.Resolver = _ => new BackdropSource(endfieldBgUrl, BackdropKind.Image);

        var captured = new List<(string Name, byte[]? Png)>();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            ctx.BackgroundHandler.Map(endfieldBgUrl, CreateTestBackgroundBytes());
            ctx.BackgroundHandler.Map(endfieldIconUrl, CreateTestIconBytes());

            RunToCompletion(() => ctx.Vm.InitializeAsync());
            var window = new MainWindow { DataContext = ctx.Vm, Width = 1120, Height = 720 };
            // headless 中迁移动画冻结在首帧会遮盖基值：截图需要指示点的终态位置
            window.NavIndicatorAnimationEnabled = false;
            window.Show();
            window.UpdateLayout();

            void Capture(string name)
            {
                Thread.Sleep(150); // headless 动画时钟靠手动 tick 推进：先等真实时钟，再显式推进
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(400);
                var frame = window.CaptureRenderedFrame();
                if (frame is null)
                {
                    captured.Add((name, null));
                    return;
                }

                using var ms = new MemoryStream();
                frame.Save(ms, new PngBitmapEncoderOptions());
                captured.Add((name, ms.ToArray()));
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

            void WaitForBackdrop()
            {
                // 背景为装饰性异步加载：轮询等待其就绪（上限 5s），避免截到纯渐变帧
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (!ctx.Vm.Games[0].HasBackgroundImage && DateTime.UtcNow < deadline)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(20);
                }
            }

            // 模拟鸣潮背景图（resolver 已 stub，直接给本地测试图文件）
            var bgPath = ctx.TempDir.FilePath("kr_game_cache", "animate_bg", "h1", "home_1.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(bgPath)!);
            CreateTestBackground(bgPath);
            ctx.KuroBackdrop.Resolver = _ => new BackdropSource(bgPath, BackdropKind.Image);
            // headless 测试进程解析不了 app 的 avares:// 资源：图标走 stub http（本地字节）
            const string wuwaIconUrl = "https://is1-ssl.mzstatic.com/wuwa-icon.jpg";
            ctx.BackgroundHandler.Map(wuwaIconUrl, CreateTestIconBytes());
            ctx.Vm.Games[0].Game.Icon = wuwaIconUrl;
            RunToCompletion(() => ctx.Vm.Games[0].RefreshAsync());

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
            File.WriteAllBytes(endfieldExe, "MZ"u8.ToArray());
            ctx.Vm.SelectedGame = ctx.Vm.Games[1];
            RunToCompletion(() => ctx.Vm.Games[1].RefreshAsync());
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
            RunToCompletion(() => ctx.Vm.Games[0].InstallOrUpdateCommand.ExecuteAsync(null));
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
            RunToCompletion(() => ctx.Vm.Games[1].RefreshAsync());
            RunToCompletion(() => ctx.Vm.Games[1].InstallOrUpdateCommand.ExecuteAsync(null)); // 安装
            RunToCompletion(() => ctx.Vm.Games[1].InstallOrUpdateCommand.ExecuteAsync(null)); // 校验 → 确认条
            window.UpdateLayout();
            Capture("09-repair-confirm-dark.png");

            window.Close();
        }, CancellationToken.None);

        // 断言与落盘在 Dispatch 外（审计修复：帧非空断言曾被吞——覆盖层坏掉时静默导出空图仍绿）
        Assert.NotEmpty(captured);
        Assert.All(captured, c => Assert.True(c.Png is not null && c.Png.Length > 0, $"截图 {c.Name} 抓帧失败"));
        foreach (var (name, png) in captured)
        {
            File.WriteAllBytes(Path.Combine(outDir, name), png!);
        }
    }

    /// <summary>
    /// 启动失败覆盖层 + 启动设置卡（Linux：umu 启动二选一 + Proton 发行版下拉 + 组件状态卡）的视觉自检
    /// （Linux 平台语义；VmFactory 未注入原生 umu 启动器/组件准备器 → 走通用预检失败出覆盖层、
    /// 准备按钮禁用）。审计修复同上：断言外移 + 异步步骤 RunJobs 泵。
    /// </summary>
    [Fact]
    public async Task Export_LaunchErrorOverlay_ForReview()
    {
        var outDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "ui-review"));
        Directory.CreateDirectory(outDir);

        // 模板经 SampleConfigJson.Replace 定向加 icon（单一事实源，先例 ToastTests）：icon 走配置，
        // 版本缓存建立后的复刷不再发射资产加载，InitializeAsync 后再设 Game.Icon 无人加载
        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson.Replace(
                "\"executable\": \"Client/Binaries/Win64/Client-Win64-Shipping.exe\",",
                "\"executable\": \"Client/Binaries/Win64/Client-Win64-Shipping.exe\",\n      \"icon\": \"https://is1-ssl.mzstatic.com/wuwa-icon.jpg\","),
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: []);
        ctx.Gryphline.VersionInfo = new ChannelVersionInfo { LatestVersion = "1.2.0" };

        var captured = new List<(string Name, byte[]? Png)>();
        var launchErrorShown = false;

        await HeadlessSession.Instance.Dispatch(() =>
        {
            // icon 字节先于 InitializeAsync 注册（首刷即加载；Build 后才 Map 会 404 且复刷不重试）
            var iconFile = ctx.TempDir.FilePath("game-icon.png");
            CreateTestBackground(iconFile);
            ctx.BackgroundHandler.Map("https://is1-ssl.mzstatic.com/wuwa-icon.jpg", File.ReadAllBytes(iconFile));

            RunToCompletion(() => ctx.Vm.InitializeAsync());
            var window = new MainWindow { DataContext = ctx.Vm, Width = 1120, Height = 720 };
            window.NavIndicatorAnimationEnabled = false;
            window.Show();
            window.UpdateLayout();

            void Capture(string name)
            {
                Thread.Sleep(150);
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(400);
                var frame = window.CaptureRenderedFrame();
                if (frame is null)
                {
                    captured.Add((name, null));
                    return;
                }

                using var ms = new MemoryStream();
                frame.Save(ms, new PngBitmapEncoderOptions());
                captured.Add((name, ms.ToArray()));
            }

            var wuwa = ctx.Vm.Games[0];

            // 空态（官方未投放背景 + 无缓存）：渐变 + 官方图标水印。
            // icon 走配置模板 + 字节先注册（见上）：版本缓存建立后的复刷不再发射资产加载
            // （RefreshAsync 仅 versionChanged/regionChanged 时发射），InitializeAsync 后再设
            // Game.Icon 无人加载——水印因此静默缺席过（2026-09-25 judge 实锤后修夹具）
            var iconDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!wuwa.HasGameIcon && DateTime.UtcNow < iconDeadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Assert.True(wuwa.HasGameIcon, "夹具前置失败：模板 icon 未就绪（空态水印截不出来）");
            Capture("12-detail-empty-state-dark.png");

            var exePath = Path.Combine(
                wuwa.InstallDirPath, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            File.WriteAllBytes(exePath, "MZ"u8.ToArray());
            RunToCompletion(() => wuwa.RefreshAsync());

            // 主程序就位 + umu 模板 + umu 未装 → 启动预检失败 → 错误覆盖层（含一键安装按钮）
            RunToCompletion(() => wuwa.LaunchCommand.ExecuteAsync(null));
            launchErrorShown = wuwa.HasLaunchError;
            window.UpdateLayout();
            Capture("10-launch-error-overlay-dark.png");

            // CanRetry=true 的覆盖层（修复轮 review：主次对调后"重试"升主钮的形态从未被视觉验收）。
            // UmuRuntimeMissing 的真实夹具需要"盘上有 Proton 但缺 Steam Runtime"，过重——
            // 视觉验收只关心视图层：直接注入 CanRetry=true 的错误 VM
            wuwa.LaunchError = new LaunchErrorViewModel(
                "Steam Runtime（steamrt）尚未安装。请在启动设置里检查兼容层组件，或启用自动下载。",
                "detail", null,
                platform: new FakePlatformInfo(isLinux: true), canRetry: true);
            window.UpdateLayout();
            Capture("10b-launch-error-retry-dark.png");
            wuwa.LaunchError = null; // 还原，避免影响后续 11/14 的画面状态
            window.UpdateLayout();

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
        }, CancellationToken.None);

        Assert.True(launchErrorShown, "启动预检失败未点亮覆盖层状态（截图会静默变成'无覆盖层'画面）");
        Assert.NotEmpty(captured);
        Assert.All(captured, c => Assert.True(c.Png is not null && c.Png.Length > 0, $"截图 {c.Name} 抓帧失败"));
        foreach (var (name, png) in captured)
        {
            File.WriteAllBytes(Path.Combine(outDir, name), png!);
        }
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

        int before = -1, after = -1;
        byte[]? png = null;

        await HeadlessSession.Instance.Dispatch(() =>
        {
            RunToCompletion(() => ctx.Vm.InitializeAsync());
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

            before = LuminanceAt(430, 130);
            RunToCompletion(() => settings.CheckProtonUpdateCommand.ExecuteAsync(null));
            after = LuminanceAt(430, 130);

            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Thread.Sleep(150);
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(400);
            var frame = window.CaptureRenderedFrame();
            if (frame is not null)
            {
                using var ms = new MemoryStream();
                frame.Save(ms, new PngBitmapEncoderOptions());
                png = ms.ToArray();
            }

            window.Close();
        }, CancellationToken.None);

        // 断言在 Dispatch 外（审计修复：纱罩压暗的像素级守卫曾被吞——覆盖层失效时测试照绿）
        Assert.True(before > 0, "纱罩前基线帧抓取失败");
        Assert.True(after < before, $"纱罩未压暗底页：before={before} after={after}");
        Assert.NotNull(png);
        File.WriteAllBytes(Path.Combine(outDir, "15-proton-update-confirm-dark.png"), png!);
    }

    /// <summary>
    /// 唤取记录页的视觉自检（评审 P3-12：17 张截图唯一缺页——恰是曾经唯一含非令牌颜色的页面）：
    /// 缓存播种五星/四星/三星记录 → 统计卡金色数字 + 列表行稀有度着色，暗/亮两张。
    /// 缓存形状抄 GachaPageWiringTests（camelCase，构造期载缓存）。
    /// </summary>
    [Fact]
    public async Task Export_GachaPage_ForReview()
    {
        var outDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "ui-review"));
        Directory.CreateDirectory(outDir);

        using var ctx = VmFactory.Build();
        var cacheDir = ctx.TempDir.FilePath("gacha-cache");
        Directory.CreateDirectory(cacheDir);
        await File.WriteAllTextAsync(Path.Combine(cacheDir, "wuthering-waves.json"), """
            { "records": [
                { "time": "2026-09-01 12:00:00", "name": "维里奈", "qualityLevel": 5, "poolType": 1 },
                { "time": "2026-08-28 21:47:03", "name": "今汐",   "qualityLevel": 5, "poolType": 1 },
                { "time": "2026-08-27 09:15:44", "name": "秧秧",   "qualityLevel": 4, "poolType": 1 },
                { "time": "2026-08-25 18:02:10", "name": "莫特斐", "qualityLevel": 4, "poolType": 1 },
                { "time": "2026-08-21 23:59:59", "name": "白莲",   "qualityLevel": 4, "poolType": 1 },
                { "time": "2026-08-20 10:00:00", "name": "游弋蝶", "qualityLevel": 3, "poolType": 1 }
            ] }
            """);

        ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.6.0" };

        var captured = new List<(string Name, byte[]? Png)>();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            RunToCompletion(() => ctx.Vm.InitializeAsync());
            var window = new MainWindow { DataContext = ctx.Vm, Width = 1120, Height = 720 };
            window.NavIndicatorAnimationEnabled = false;
            window.Show();

            void Capture(string name)
            {
                Thread.Sleep(150);
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(400);
                var frame = window.CaptureRenderedFrame();
                if (frame is null)
                {
                    captured.Add((name, null));
                    return;
                }

                using var ms = new MemoryStream();
                frame.Save(ms, new PngBitmapEncoderOptions());
                captured.Add((name, ms.ToArray()));
            }

            ctx.Vm.ShowGachaCommand.Execute(ctx.Vm.Games[0]);
            window.UpdateLayout();
            Capture("17-gacha-page-dark.png");

            var light = ctx.Vm.ThemeModes.First(t => t.Mode == ThemeMode.Light);
            ctx.Vm.SelectedTheme = light;
            window.UpdateLayout();
            Capture("18-gacha-page-light.png");

            window.Close();
        }, CancellationToken.None);

        var gacha = Assert.IsType<GachaViewModel>(ctx.Vm.CurrentPage);
        Assert.Equal(6, gacha.Records.Count);
        Assert.NotEmpty(captured);
        Assert.All(captured, c => Assert.True(c.Png is not null && c.Png.Length > 0, $"截图 {c.Name} 抓帧失败"));
        foreach (var (name, png) in captured)
        {
            File.WriteAllBytes(Path.Combine(outDir, name), png!);
        }
    }

    [Fact]
    public async Task Export_BootSplash_ForReview()
    {
        var outDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "ui-review"));
        Directory.CreateDirectory(outDir);

        using var ctx = VmFactory.Build();
        // 模拟 App 启动路径：窗口上屏前开启遮蔽，初始化在遮蔽后进行
        ctx.Vm.BeginBootSplash();

        var splashVisible = false;
        byte[]? png = null;

        await HeadlessSession.Instance.Dispatch(() =>
        {
            RunToCompletion(() => ctx.Vm.InitializeAsync());
            var window = new MainWindow { DataContext = ctx.Vm, Width = 1120, Height = 720 };
            window.NavIndicatorAnimationEnabled = false;
            window.SplashAnimationEnabled = false; // headless 冻结脉动条：关泵取静态帧
            window.Show();
            window.UpdateLayout();

            var splash = window.GetVisualDescendants()
                .OfType<Panel>()
                .FirstOrDefault(p => p.Name == "BootSplash");
            Assert.NotNull(splash);
            splashVisible = splash.IsVisible;

            Thread.Sleep(150);
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(400);
            var frame = window.CaptureRenderedFrame();
            if (frame is not null)
            {
                using var ms = new MemoryStream();
                frame.Save(ms, new PngBitmapEncoderOptions());
                png = ms.ToArray();
            }

            window.Close();
        }, CancellationToken.None);

        Assert.True(splashVisible, "启动遮蔽层未随 DataContext 点亮");
        Assert.NotNull(png);
        File.WriteAllBytes(Path.Combine(outDir, "16-boot-splash-dark.png"), png!);
    }

    /// <summary>
    /// 终末地详情页 × 本机真实缓存海报的视觉自检（2026-09-25 纱带压暗验收）：
    /// 官方海报左上角烧录"明日方舟 终末地"Logo 字，自绘标题压在其上——假图测不到，
    /// 必须用真海报看纱带（240 高、顶部 0.70）能否把 Logo 压暗隐入背景。
    /// 机器前提：本机存在真实下载缓存（CI 没有 → Skip；符合"依赖机器有 X 的测试在缺失时跳过"纪律）。
    /// </summary>
    [Fact]
    public async Task Export_EndfieldRealBackdrop_ForReview()
    {
        // 经 AppPaths 单一事实源（尊重 XDG_DATA_HOME/平台差异），硬编码家目录会在改策略后永久 Skip
        var posterPath = Path.Combine(
            YetAnotherGameLauncher.Core.AppPaths.DataDirectory, "backdrops", "arknights-endfield", "poster.png");
        if (!File.Exists(posterPath))
        {
            Assert.Skip($"本机无终末地真实海报缓存（{posterPath}），真海报视觉验收仅在有缓存的机器运行");
        }

        var outDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "ui-review"));
        Directory.CreateDirectory(outDir);

        using var ctx = VmFactory.Build();
        ctx.Gryphline.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" };
        var backdropReady = false;
        byte[]? png = null;

        await HeadlessSession.Instance.Dispatch(() =>
        {
            // resolver 必须在 InitializeAsync 前种好：版本缓存命中后的复刷不再发射资产加载
            ctx.GryphlineBackdrop.Resolver = _ => new YetAnotherGameLauncher.Core.Abstractions.BackdropSource(
                posterPath, YetAnotherGameLauncher.Core.Abstractions.BackdropKind.Image);

            RunToCompletion(() => ctx.Vm.InitializeAsync());
            ctx.Vm.SelectedGame = ctx.Vm.Games[1]; // 终末地

            // LoadAssetsCoreAsync fire-and-forget：有界轮询背景落地
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!ctx.Vm.Games[1].HasBackgroundImage && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            backdropReady = ctx.Vm.Games[1].HasBackgroundImage;

            // detected 态（主程序在盘未登记）：空态引导卡隐藏、状态 chips 显示——
            // 与用户报告重叠时的画面同态（空态卡会遮住 Logo 区干扰验收）
            var endfieldExe = Path.Combine(ctx.Vm.Games[1].InstallDirPath, "Endfield.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(endfieldExe)!);
            File.WriteAllBytes(endfieldExe, "MZ"u8.ToArray());
            var refresh = ctx.Vm.Games[1].RefreshAsync();
            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!refresh.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            var window = new MainWindow { DataContext = ctx.Vm, Width = 1120, Height = 720 };
            window.NavIndicatorAnimationEnabled = false;
            window.Show();
            window.UpdateLayout();

            Thread.Sleep(150);
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(400);
            var frame = window.CaptureRenderedFrame();
            if (frame is not null)
            {
                using var ms = new MemoryStream();
                frame.Save(ms, new PngBitmapEncoderOptions());
                png = ms.ToArray();
            }

            window.Close();
        }, CancellationToken.None);

        Assert.True(backdropReady, "夹具前置失败：真实海报未就绪");
        Assert.NotNull(png);
        File.WriteAllBytes(Path.Combine(outDir, "19-endfield-real-backdrop-dark.png"), png!);
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

    /// <summary>在内存中渲染同一渐变图（"远程"字节的本地替代）。</summary>
    private static byte[] CreateTestBackgroundBytes()
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

        using var ms = new MemoryStream();
        rtb.Save(ms, new PngBitmapEncoderOptions());
        return ms.ToArray();
    }

    /// <summary>在内存中渲染纯色小方块图标（"远程"字节的本地替代）。</summary>
    private static byte[] CreateTestIconBytes()
    {
        using var rtb = new RenderTargetBitmap(new PixelSize(256, 256));
        using (var dc = rtb.CreateDrawingContext())
        {
            dc.FillRectangle(new SolidColorBrush(Color.FromRgb(0x44, 0x8F, 0xF4)), new Rect(0, 0, 256, 256));
        }

        using var ms = new MemoryStream();
        rtb.Save(ms, new PngBitmapEncoderOptions());
        return ms.ToArray();
    }
}
