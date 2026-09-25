using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 游戏详情页相关无头回归：
/// 1) 玻璃按钮（glass-onart）浅色前景必须在禁用/悬停态保持——Fluent 的状态样式把主题前景
///    （亮色主题为黑/灰）直接写在模板 presenter 层，会压过 Button 本体上的任何前景值，
///    因此 App 的浅色前景必须同样下沉到 presenter 层（曾经正是禁用启动按钮在亮色主题黑字）；
/// 2) 详情页全出血布局类（detail）随页面类型切换，其余页面保持浮卡。
/// </summary>
[Collection("sequential")]
public class DetailPageHeadlessTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public DetailPageHeadlessTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task GlassOnArtButton_KeepsLightForeground_InDisabledAndHoverStates()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.RequestedThemeVariant = ThemeVariant.Light; // 黑字问题只在亮色主题暴露
            window.Show();
            window.UpdateLayout();

            // P1-2 起未安装时启动钮整体隐藏（模板不实例化），禁用态浅色前景的回归
            // 改由探针按钮承载（样式四态相同）；可见性语义归 DetailPageIdentityTests
            var probe = new Button { Classes = { "glass-onart" }, Width = 80, Height = 36 };
            ((Panel)window.Content!).Children.Add(probe);
            window.UpdateLayout();
            var probePresenter = TemplatePresenter(probe);
            Assert.Equal(ArtworkColor(window, "AppOnArtworkBrush"), PresenterColor(probePresenter));

            probe.IsEnabled = false;
            window.UpdateLayout();
            Assert.Equal(ArtworkColor(window, "AppOnArtworkSecondary"), PresenterColor(probePresenter));

            probe.IsEnabled = true;
            window.UpdateLayout();
            Assert.Equal(ArtworkColor(window, "AppOnArtworkBrush"), PresenterColor(probePresenter));

            var center = probe.TranslatePoint(new Point(probe.Bounds.Width / 2, probe.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            window.UpdateLayout();
            Assert.True(probe.IsPointerOver);
            Assert.Equal(ArtworkColor(window, "AppOnArtworkBrush"), PresenterColor(probePresenter));
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ContentCard_UniformPageSheet_BelowTitleBand()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var card = window.FindControl<Border>("ContentCard")!;
            var drag = window.FindControl<Border>("TitleDrag")!;
            var chrome = window.FindControl<Border>("TitleChrome")!;

            // 页面板（所有页面统一，同详情页海报）：色带下方开始、左上角圆角、贴边全出血；
            // 色带 46 高；拖拽条覆盖色带整条
            Assert.Equal(new Thickness(0, 46, 0, 0), card.Margin);
            Assert.Equal(new CornerRadius(10, 0, 0, 0), card.CornerRadius);
            Assert.Equal(56, chrome.Height);
            Assert.Equal(46, drag.Height);

            // 游戏设置页 / 应用设置页：同一页面板布局，容器透明露出页面板内的应用背景
            _ctx.Vm.ShowGameSettingsCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal(new Thickness(0, 46, 0, 0), card.Margin);

            _ctx.Vm.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            Assert.Null(card.Background);
            var page = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("page"));
            Assert.Equal(new Thickness(32, 48, 32, 24), page.Margin); // 2026-09-23 刻度审计：34→32 对齐 4pt 网格

            // 返回详情页：同板
            _ctx.Vm.ShowGamesCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal(new Thickness(0, 46, 0, 0), card.Margin);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GameDetailPage_PosterImage_IsLeftAnchored()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            // 海报靠左上锚定：UniformToFill 的居中裁切曾吃掉海报左缘（被感知为"被侧边栏遮住"）
            var poster = window.GetVisualDescendants().OfType<Image>().First(i => i.Name == "PosterImage");
            Assert.Equal(HorizontalAlignment.Left, poster.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Top, poster.VerticalAlignment);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GameDetailPage_ChipsRow_ProposalALayout()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var page = window.GetVisualDescendants().OfType<Panel>().First(p => p.Classes.Contains("page"));

            // 顶部 chips：状态胶囊与版本号分段胶囊（onart-chip 族），位于详情页顶部左侧
            var chips = page.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("onart-chip")).ToList();
            var statusChip = chips.Single(c => c.Child?.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == _ctx.Vm.Games[0].StatusText) == true);
            var chipOrigin = statusChip.TranslatePoint(new Point(0, 0), page)!.Value;
            Assert.True(chipOrigin.X < 60 && chipOrigin.Y < 120,
                $"chips 行应落在详情页左上角，实际 {chipOrigin}");
            var texts = page.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Contains(texts, t => t.Text == _ctx.Vm.Games[0].VersionChipLead);
            Assert.Contains(texts, t => t.Text == _ctx.Vm.Games[0].VersionChipNumber);
            // 顶部纱带：全出血渐变底，不拦交互；固定高 140 盖住顶部 chips 簇
            //（自绘标题移除后纱带职责只剩 chips 可读底，2026-09-25 由 30% 比例回调固定值）
            var scrim = page.GetVisualDescendants().OfType<Border>()
                .First(b => ReferenceEquals(b.Background, window.FindResource("AppOnArtworkScrimBrush")));
            Assert.False(scrim.IsHitTestVisible);
            Assert.Equal(140, scrim.Height);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GameDetailPage_Scrim_DimsArtworkBehindTitleCluster()
    {
        // 纱带的职责是给 chips 身后的插画垫可读底：
        // 平色亮背景上同列采样，带内点必须显著暗于带外点（像素级守卫，先例 ProtonUpdateConfirm）。
        // resolver 必须在 InitializeAsync 前种好：版本缓存命中后的复刷不再发射资产加载
        // （LoadAssetsCoreAsync 还是 fire-and-forget），后种缝永远等不到背景（StartupAssetPreloadTests 同款）
        var bgPath = _ctx.TempDir.FilePath("kr_game_cache", "animate_bg", "h1", "flat.jpg");

        var inBand = -1.0;
        var belowBand = -1.0;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(bgPath)!);
            using (var rtb = new RenderTargetBitmap(new PixelSize(960, 640)))
            {
                using (var dc = rtb.CreateDrawingContext())
                {
                    dc.FillRectangle(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)), new Rect(0, 0, 960, 640));
                }

                rtb.Save(bgPath, new PngBitmapEncoderOptions());
            }

            _ctx.KuroBackdrop.Resolver = _ => new BackdropSource(bgPath, BackdropKind.Image);

            var init = _ctx.Vm.InitializeAsync();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!init.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }

            Assert.True(init.IsCompleted, "InitializeAsync 30s 未完成（RunJobs 泵停摆）");

            // LoadAssetsCoreAsync fire-and-forget：有界轮询背景落地（HasBackgroundImage 由加载完成后置位）
            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!_ctx.Vm.Games[0].HasBackgroundImage && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Assert.True(_ctx.Vm.Games[0].HasBackgroundImage, "夹具前置失败：平色背景未就绪（海报渲染不了，纱带探针会量到深色底层）");

            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

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

            // x=680：chips 行 WrapPanel（x≈28 起、MaxWidth 640）最右到 x≈668——采样列在其右侧，
            // 防未来长文案 chip 盖住采样点后 chip 自身暗底把守卫喂成假绿；y=60 带内上部
            //（0.55 顶渐变，此处 α≈0.31）、y=450 带外纯背景（EmptyStateCard MaxWidth 480 够不到）。
            // 先按截图测试同款等待真实时钟再推渲染计时器：首拍可能还没把海报画出来
            Thread.Sleep(150);
            inBand = LuminanceAt(680, 60);
            belowBand = LuminanceAt(680, 450);
            window.Close();
        }, CancellationToken.None);

        Assert.True(inBand > 0, "带内采样点抓帧失败");
        // belowBand 下限钉住"海报确实渲染了"：深底（~90）会让压暗比较失去判别力
        Assert.True(belowBand > 400, $"带外采样点过暗，平色海报可能未渲染：belowBand={belowBand}");
        Assert.True(inBand < belowBand * 0.8,
            $"纱带未压暗 chips 身后插画：inBand={inBand} belowBand={belowBand}");
    }

    [Fact]
    public async Task GameDetailPage_ArtworkFallback_HiddenWhenVideoPlaysWithoutPoster()
    {
        // 海报加载失败但背景视频正常起播：不透明的无插画回退层（深色渐变+图标水印）
        // 位于视频层之上，可见性只看「无海报」不看「无视频」会整面盖住视频（2026-09-25 修复）
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.Show();
            window.UpdateLayout();

            var fallback = window.GetVisualDescendants().OfType<Grid>()
                .First(g => g.Name == "ArtworkFallback");

            // 基线：无海报无视频（夹具默认）→ 回退层可见（渐变+水印，空态设计）
            Assert.False(_ctx.Vm.Games[0].HasBackgroundImage, "夹具前置失败：默认夹具不应有海报");
            Assert.True(fallback.IsVisible, "无海报无视频时回退层必须可见（渐变+水印空态底）");

            _ctx.Vm.Games[0].HasBackgroundVideo = true;
            window.UpdateLayout();

            Assert.False(fallback.IsVisible, "有背景视频时不得显示无插画回退层（会盖住视频）");
            var video = window.GetVisualDescendants()
                .OfType<YetAnotherGameLauncher.Controls.FrameSurface>().First();
            Assert.True(video.IsVisible);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ActionDock_ValueColumn_KeepsLightForeground_InLightTheme()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            var window = new MainWindow { DataContext = _ctx.Vm };
            window.RequestedThemeVariant = ThemeVariant.Light; // 黑字叠黑底只在亮色主题暴露
            window.Show();
            window.UpdateLayout();

            // 坞底恒为深色玻璃：值列若继承主题前景（亮色 = 近黑）会黑字叠黑底（judge 02 实锤）。
            // 与 GlassOnArtButton_KeepsLightForeground 同根因，此断言防"清理冗余 Foreground"式回归。
            // P1-2 后未安装态另有空态引导卡展示同名字段（同样的浅色前景），一并纳入断言
            var valueTexts = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Text == _ctx.Vm.Games[0].ChannelDisplayName
                            || t.Text == _ctx.Vm.Games[0].ServerCountText
                            || t.Text == _ctx.Vm.Games[0].InstallDirPath)
                .ToList();
            Assert.True(valueTexts.Count >= 3, $"坞/空态卡值列文本数不足：{valueTexts.Count}");
            foreach (var value in valueTexts)
            {
                Assert.Equal(ArtworkColor(window, "AppOnArtworkBrush"),
                    Assert.IsType<SolidColorBrush>(value.Foreground).Color);
            }

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GameDetailPage_ChipsRow_LongStatus_WrapsInsteadOfClipping()
    {
        await _ctx.Vm.InitializeAsync();

        await HeadlessSession.Instance.Dispatch(() =>
        {
            // MinWidth 必须一并解除（XAML 写死 920）：窗口钳回 920 时侧栏自动收起，
            // 内容区 852px 连最长 chips 行（约 790px）都放得下，换行路径根本不会被逼出来
            var window = new MainWindow { DataContext = _ctx.Vm, MinWidth = 0, Width = 640, Height = 720 };
            window.Show();
            window.UpdateLayout();

            var wuwa = _ctx.Vm.Games[0];
            wuwa.StatusText = new string('长', 40); // 启动预检级别的长文案（约 2 行）
            window.UpdateLayout();

            // 版本 chip 必须换到状态 chip 的下一行（而非同排溢出被内容卡裁掉），且完整落在页面板内。
            // 回归对照：水平 StackPanel 行会让版本 chip 原样排在长状态同排溢出视口
            var page = window.GetVisualDescendants().OfType<Panel>().First(p => p.Classes.Contains("page"));
            var statusText = page.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == wuwa.StatusText);
            var number = page.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == wuwa.VersionChipNumber);
            var statusTop = statusText.TranslatePoint(new Point(0, 0), page)!.Value.Y;
            var numberTop = number.TranslatePoint(new Point(0, 0), page)!.Value.Y;
            Assert.True(numberTop > statusTop, "版本 chip 未换行：与长状态 chip 仍在同一排");
            var right = number.TranslatePoint(new Point(number.Bounds.Width, 0), page)!.Value.X;
            Assert.True(right <= page.Bounds.Width,
                $"版本号右缘 {right:0} 超出页面板 {page.Bounds.Width:0}，chips 行未换行");

            wuwa.StatusText = "";
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>取按钮模板里的内容呈现器（Fluent 与 App 状态样式的共同作用点）。</summary>
    private static ContentPresenter TemplatePresenter(Button button) =>
        button.GetVisualDescendants().OfType<ContentPresenter>()
            .First(p => p.Name == "PART_ContentPresenter");

    private static Color PresenterColor(ContentPresenter presenter) =>
        Assert.IsType<SolidColorBrush>(presenter.Foreground).Color;

    private static Color ArtworkColor(MainWindow window, string key) =>
        Assert.IsType<SolidColorBrush>(window.FindResource(key)).Color;
}
