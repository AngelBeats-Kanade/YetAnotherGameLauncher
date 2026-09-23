using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;
using YetAnotherGameLauncher.AppTests;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 主题令牌对比度门禁（2026-09-23 UI 设计评审 P2-7/P2-8 的固化）：
/// 文字类令牌以 WCAG 2.1 §1.4.3 正文线 4.5:1 为准，半透明底先按 alpha 与
/// 最坏背景（白海报 #FFFFFF / 暗主题背板最亮 stop #131A2E）合成再计算。
/// 新增或改动 App.axaml 颜色令牌必须保持本门禁全绿；禁用形如"调透明度绕过"的回归。
/// </summary>
[Collection("sequential")]
public class ThemeTokenContrastTests
{
    /// <summary>WCAG 正文对比度线（14–16px 按钮字/12–13px 标签字均适用）。</summary>
    private const double BodyTextRatio = 4.5;

    /// <summary>白海报：半透明底的最坏亮背景（评审 §3 口径）。</summary>
    private static readonly Color PosterWhite = Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>暗主题背板渐变的最亮 stop：浅色字压暗底的最坏背景。</summary>
    private static readonly Color DarkBackdropLightest = Color.FromRgb(0x13, 0x1A, 0x2E);

    [Fact]
    public async Task AccentButtons_WhiteText_MeetsContrast_InBothThemes()
    {
        var light = await ResolveAsync(ThemeVariant.Light, "AppAccentBrush", "AppAccentHoverBrush");
        var dark = await ResolveAsync(ThemeVariant.Dark, "AppAccentBrush", "AppAccentHoverBrush");

        AssertContrast("亮色 accent 白字", Colors.White, light["AppAccentBrush"]);
        AssertContrast("亮色 accent hover 白字", Colors.White, light["AppAccentHoverBrush"]);
        AssertContrast("暗色 accent 白字", Colors.White, dark["AppAccentBrush"]);
        AssertContrast("暗色 accent hover 白字", Colors.White, dark["AppAccentHoverBrush"]);
    }

    [Fact]
    public async Task PredownloadBadge_TextOnAmber_MeetsContrast()
    {
        // 徽章底为主题无关琥珀：任意主题解析一次即可（曾以白字 2.44:1 为全表最差）
        var tokens = await ResolveAsync(ThemeVariant.Light, "AppPredownloadBadge", "AppPredownloadBadgeText");

        AssertContrast("已暂存徽章深字 on 琥珀", tokens["AppPredownloadBadgeText"], tokens["AppPredownloadBadge"]);
    }

    [Fact]
    public async Task VersionChip_AccentAndText_MeetContrast_OverWorstBackgrounds()
    {
        var keys = new[] { "AppVersionChipBackground", "AppVersionChipAccent", "AppVersionChipText" };
        var light = await ResolveAsync(ThemeVariant.Light, keys);
        var dark = await ResolveAsync(ThemeVariant.Dark, keys);

        // 亮色 chip（90% 白底）：最坏 = 压白海报（底最亮）与压暗海报（底变中灰）
        AssertContrast("亮色版本金×白海报", light["AppVersionChipAccent"], Composite(PosterWhite, light["AppVersionChipBackground"]));
        AssertContrast("亮色版本金×暗海报", light["AppVersionChipAccent"], Composite(DarkBackdropLightest, light["AppVersionChipBackground"]));
        AssertContrast("亮色版本正文×暗海报", light["AppVersionChipText"], Composite(DarkBackdropLightest, light["AppVersionChipBackground"]));

        // 暗色 chip（70% 黑底）：最坏 = 压白海报（底变中灰，曾 3.74:1）
        AssertContrast("暗色版本金×白海报", dark["AppVersionChipAccent"], Composite(PosterWhite, dark["AppVersionChipBackground"]));
        AssertContrast("暗色版本金×暗海报", dark["AppVersionChipAccent"], Composite(DarkBackdropLightest, dark["AppVersionChipBackground"]));
        AssertContrast("暗色版本正文×白海报", dark["AppVersionChipText"], Composite(PosterWhite, dark["AppVersionChipBackground"]));
    }

    [Fact]
    public async Task Dock_CaptionAndValue_MeetContrast_OverWorstBackgrounds()
    {
        // 操作坞玻璃底恒为深色：最坏背景 = 白海报透入（坞标签曾 3.24:1）
        var tokens = await ResolveAsync(ThemeVariant.Light, "AppOnArtworkDockBrush", "AppOnArtworkTertiary", "AppOnArtworkBrush");
        var dockOverWhite = Composite(PosterWhite, tokens["AppOnArtworkDockBrush"]);
        var dockOverDark = Composite(DarkBackdropLightest, tokens["AppOnArtworkDockBrush"]);

        AssertContrast("坞标签×白海报", tokens["AppOnArtworkTertiary"], dockOverWhite);
        AssertContrast("坞标签×暗海报", tokens["AppOnArtworkTertiary"], dockOverDark);
        AssertContrast("坞值列×白海报", tokens["AppOnArtworkBrush"], dockOverWhite);
        AssertContrast("坞值列×暗海报", tokens["AppOnArtworkBrush"], dockOverDark);
    }

    [Fact]
    public async Task GachaRarity_Colors_MeetContrast_OnCards()
    {
        // 稀有度色同时供 24px 统计数字与 ~12px 列表行复用，按最严消费者取正文线；
        // 唤取页卡片压在应用背板上（无海报），最坏 = 暗主题最亮背板 stop / 亮色近纯白卡
        var light = await ResolveAsync(ThemeVariant.Light, "AppGachaRare5", "AppGachaRare4", "AppCardBackground");
        var dark = await ResolveAsync(ThemeVariant.Dark, "AppGachaRare5", "AppGachaRare4", "AppCardBackground");
        var lightCard = Composite(PosterWhite, light["AppCardBackground"]);
        var darkCard = Composite(DarkBackdropLightest, dark["AppCardBackground"]);

        AssertContrast("亮色五星金 on 亮卡", light["AppGachaRare5"], lightCard);
        AssertContrast("亮色四星紫 on 亮卡", light["AppGachaRare4"], lightCard);
        AssertContrast("暗色五星金 on 暗卡", dark["AppGachaRare5"], darkCard);
        AssertContrast("暗色四星紫 on 暗卡", dark["AppGachaRare4"], darkCard);
    }

    [Fact]
    public async Task ErrorText_OnErrorCard_MeetsContrast_InBothThemes()
    {
        // 钉住既有达标值（评审 §3 #10 口径）：错误卡近实心，最坏背景同上
        var light = await ResolveAsync(ThemeVariant.Light, "AppErrorText", "AppErrorCardBackground");
        var dark = await ResolveAsync(ThemeVariant.Dark, "AppErrorText", "AppErrorCardBackground");

        AssertContrast("亮色错误字 on 错误卡", light["AppErrorText"], Composite(PosterWhite, light["AppErrorCardBackground"]));
        AssertContrast("暗色错误字 on 错误卡", dark["AppErrorText"], Composite(DarkBackdropLightest, dark["AppErrorCardBackground"]));
    }

    /// <summary>
    /// 按指定主题解析令牌颜色。
    /// 必须经 Application.TryGetResource 显式传主题变体：ThemeDictionaries 只按显式主题选择，
    /// FindResource/窗口宿主不参与字典选择（探针实证：窗口 FindResource 对主题字典恒返回 UnsetValue）。
    /// </summary>
    private static async Task<Dictionary<string, Color>> ResolveAsync(ThemeVariant variant, params string[] keys)
    {
        var resolved = new Dictionary<string, Color>();
        await HeadlessSession.Instance.Dispatch(() =>
        {
            foreach (var key in keys)
            {
                if (Application.Current!.TryGetResource(key, variant, out var value)
                    && value is SolidColorBrush brush)
                {
                    resolved[key] = brush.Color;
                }
            }
        }, CancellationToken.None);

        // 断言一律在 Dispatch 外：缺键必须落在断言上而非静默跳过
        foreach (var key in keys)
        {
            Assert.True(resolved.ContainsKey(key), $"主题令牌 {key} 未定义（{variant}）");
        }

        return resolved;
    }

    /// <summary>把 overlay 按 alpha 叠在 backdrop 上，得到不透明合成色。</summary>
    private static Color Composite(Color backdrop, Color overlay)
    {
        double alpha = overlay.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(alpha * overlay.R + (1 - alpha) * backdrop.R),
            (byte)Math.Round(alpha * overlay.G + (1 - alpha) * backdrop.G),
            (byte)Math.Round(alpha * overlay.B + (1 - alpha) * backdrop.B));
    }

    /// <summary>断言前景/背景对比度达标（失败信息含实测比率与两端色值，便于定点修令牌）。</summary>
    private static void AssertContrast(string label, Color foreground, Color background, double min = BodyTextRatio)
    {
        double ratio = ContrastRatio(foreground, background);
        Assert.True(ratio >= min,
            $"{label} 对比度 {ratio:0.00}:1 < {min}:1（#{foreground.R:X2}{foreground.G:X2}{foreground.B:X2} on #{background.R:X2}{background.G:X2}{background.B:X2}）");
    }

    /// <summary>WCAG 2.1 相对亮度（sRGB 线性化）。</summary>
    private static double RelativeLuminance(Color c)
    {
        static double Linear(byte channel)
        {
            double v = channel / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    /// <summary>WCAG 对比度：(亮侧 L + 0.05) / (暗侧 L + 0.05)。</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
