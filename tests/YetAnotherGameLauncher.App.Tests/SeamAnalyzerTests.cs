using Xunit;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 循环接缝分析核心 v2：RGB 缩略的降采样、帧对综合分（全局平均 + 分块最大惩罚）、
/// 循环点搜索（阈值/最小循环时长/同分取最早头帧/排名与头帧间距去重/降级兜底）、
/// 时序连续性项与硬化切/自适应淡化策略边界。
/// </summary>
public class SeamAnalyzerTests
{
    /// <summary>构造一幅恒定颜色（B,G,R）的 BGRA 帧。</summary>
    private static byte[] SolidBgra(int width, int height, byte blue, byte green, byte red)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = blue;
            pixels[i * 4 + 1] = green;
            pixels[i * 4 + 2] = red;
        }

        return pixels;
    }

    [Fact]
    public void Downsample_WhiteFrame_ProducesFullWhite()
    {
        var frame = SolidBgra(64, 36, 255, 255, 255);

        var rgb = SeamAnalyzer.DownsampleBgraToRgb(frame, 64, 36, 64 * 4, SeamAnalyzer.ThumbWidth, SeamAnalyzer.ThumbHeight);

        Assert.Equal(SeamAnalyzer.ThumbWidth * SeamAnalyzer.ThumbHeight * SeamAnalyzer.ThumbChannels, rgb.Length);
        Assert.All(rgb, v => Assert.Equal(255, v));
    }

    [Fact]
    public void Downsample_PureBlue_KeepsColorInChannels()
    {
        // v2 保留彩色：纯蓝（B=255）的缩略 R=0、G=0、B=255——灰度版会坍缩成亮度 29 漏掉色偏
        var frame = SolidBgra(32, 18, 255, 0, 0);

        var rgb = SeamAnalyzer.DownsampleBgraToRgb(frame, 32, 18, 32 * 4, 8, 8);

        for (var i = 0; i < 8 * 8; i++)
        {
            Assert.Equal(0, rgb[i * 3]);
            Assert.Equal(0, rgb[i * 3 + 1]);
            Assert.Equal(255, rgb[i * 3 + 2]);
        }
    }

    [Fact]
    public void Downsample_HorizontalGradient_IsMonotonicAcrossX()
    {
        // 每列一个灰度（B,G,R 同值随 x 递增）：缩略后横向应单调不减
        const int width = 128;
        var frame = new byte[width * 8 * 4];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < 8; y++)
            {
                var o = (y * width + x) * 4;
                frame[o] = (byte)(x * 2);
                frame[o + 1] = (byte)(x * 2);
                frame[o + 2] = (byte)(x * 2);
            }
        }

        var rgb = SeamAnalyzer.DownsampleBgraToRgb(frame, width, 8, width * 4, 16, 4);

        for (var y = 0; y < 4; y++)
        {
            for (var x = 1; x < 16; x++)
            {
                Assert.True(rgb[(y * 16 + x) * 3] >= rgb[(y * 16 + x - 1) * 3]);
            }
        }
    }

    [Fact]
    public unsafe void Downsample_PointerOverload_MatchesSpanOverload()
    {
        // 指针重载是 span 版的薄包装（FfmpegVideoBackdropPlayer 的不安全回读路径调用）：
        // 两者结果必须逐字节一致
        var frame = SolidBgra(16, 8, 10, 200, 30);
        var expected = SeamAnalyzer.DownsampleBgraToRgb(frame, 16, 8, 16 * 4, 4, 4);

        byte[] actual;
        fixed (byte* pointer = frame)
        {
            actual = SeamAnalyzer.DownsampleBgraToRgb(pointer, 16, 8, 16 * 4, 4, 4);
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Difference_IdenticalThumbs_IsZero()
    {
        var a = Solid(10);

        Assert.Equal(0, SeamAnalyzer.Difference(a, a));
    }

    [Fact]
    public void Difference_ConstantOffset_CompositesGlobalAndBlock()
    {
        // 全图恒定偏移 d：全局均值 = d，最差块均值 = d → 综合分 = d × (1 + 权重)
        var a = Solid(50);
        var b = Solid(60);

        var score = SeamAnalyzer.Difference(a, b);

        Assert.Equal(10 * (1 + SeamAnalyzer.BlockPenaltyWeight), score, 5);
    }

    [Fact]
    public void Difference_LengthMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => SeamAnalyzer.Difference(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3, 4, 5, 6 }));
    }

    [Fact]
    public void Difference_EmptyThumbs_ReturnsZero()
    {
        // 零长防御：空缩略（退化帧）不产生除零，按 0 差异处理
        Assert.Equal(0, SeamAnalyzer.Difference([], []));
    }

    [Fact]
    public void Difference_LocalizedJump_ScoresFarAboveUniformOffset()
    {
        // 同量级的整体差异 vs 集中在一小块的剧烈差异：局部跳变（主体挪了、背景没动）
        // 必须被分块惩罚显著放大——纯全局平均会把它和缓变混为一谈
        var uniform = Solid(60); // 相对基准 50：全局偏移 10
        var localized = Solid(50);
        MarkBlock(localized, value: 255); // 单块拉满：全局均值仅 ~8

        var uniformScore = SeamAnalyzer.Difference(Solid(50), uniform);
        var localizedScore = SeamAnalyzer.Difference(Solid(50), localized);

        Assert.True(localizedScore > uniformScore * 3,
            $"localized {localizedScore} should far exceed uniform {uniformScore}");
    }

    [Fact]
    public void Difference_ColorPopWithSameLuma_IsDetected()
    {
        // 灰度版漏掉的色偏：两幅缩略亮度几乎相同但颜色不同（暖白 vs 冷白），
        // RGB 综合分必须显著高于阈值（v1 灰度 SAD 在此场景约为 0）
        var neutral = SolidRgb(100, 100, 100);
        var warm = SolidRgb(154, 73, 73); // BT.601 亮度 ≈ 100，色相完全不同

        Assert.True(SeamAnalyzer.Difference(neutral, warm) > SeamAnalyzer.SeamMatchThreshold);
    }

    [Fact]
    public void FindLoopPoint_PicksMatchingPair()
    {
        // 头帧 1 与尾帧 0 完全相同，其余帧彼此不同（时序项计入后续帧的小差异，仍远低于阈值）
        var heads = new List<byte[]> { Solid(0), Solid(100), Solid(200) };
        var tails = new List<byte[]> { Solid(100), Solid(210) };
        var headPts = new List<double> { 0.0, 0.5, 1.0 };
        var tailPts = new List<double> { 9.0, 9.5 };

        var match = SeamAnalyzer.FindLoopPoint(heads, headPts, tails, tailPts);

        Assert.NotNull(match);
        Assert.Equal(1, match.HeadIndex);
        Assert.Equal(0, match.TailIndex);
        Assert.False(match.Degraded);
        Assert.True(match.Difference < SeamAnalyzer.SeamMatchThreshold);
    }

    [Fact]
    public void FindLoopPoint_RejectsPairsShorterThanMinLoop_FallsBackDegraded()
    {
        // 唯一相同的帧对间距只有 0.5 秒（低于最小循环时长）：被拒后没有阈值内候选，
        // 返回降级最优（v1 返回 null → 整段循环的接缝完全未分析；v2 兜底杜绝该路径）
        var heads = new List<byte[]> { Solid(0), Solid(100) };
        var tails = new List<byte[]> { Solid(100) };
        var headPts = new List<double> { 0.0, 1.0 };
        var tailPts = new List<double> { 1.5 };

        var match = SeamAnalyzer.FindLoopPoint(heads, headPts, tails, tailPts);

        Assert.NotNull(match);
        Assert.True(match.Degraded);
        Assert.Equal(0, match.HeadIndex); // 被拒的是 (1,0)；降级最优取 (0,0)
    }

    [Fact]
    public void FindLoopPoint_NoMatchAboveThreshold_ReturnsDegradedBest()
    {
        // 所有帧对差异 100，阈值用默认 6 → 无命中：降级返回全局最优而非 null
        var heads = new List<byte[]> { Solid(0), Solid(10) };
        var tails = new List<byte[]> { Solid(100) };
        var headPts = new List<double> { 0.0, 0.5 };
        var tailPts = new List<double> { 9.0 };

        var match = SeamAnalyzer.FindLoopPoint(heads, headPts, tails, tailPts);

        Assert.NotNull(match);
        Assert.True(match.Degraded);
    }

    [Fact]
    public void FindLoopPoint_TieBreaksToEarliestHead()
    {
        // 头帧 0 与头帧 1 都与尾帧完全相同：同分取最早头帧（循环更长；排名稳定排序保序）
        var heads = new List<byte[]> { Solid(50), Solid(50) };
        var tails = new List<byte[]> { Solid(50) };
        var headPts = new List<double> { 0.0, 0.5 };
        var tailPts = new List<double> { 9.0 };

        var match = SeamAnalyzer.FindLoopPoint(heads, headPts, tails, tailPts);

        Assert.NotNull(match);
        Assert.Equal(0, match.HeadIndex);
    }

    [Fact]
    public void FindLoopPoint_EmptyInput_ReturnsNull()
    {
        Assert.Null(SeamAnalyzer.FindLoopPoint([], [], [Solid(0)], [1.0]));
        Assert.Null(SeamAnalyzer.FindLoopPoint([Solid(0)], [1.0], [], []));
    }

    [Fact]
    public void FindLoopPoints_ReturnsFirstOfRanking()
    {
        // 单候选便捷入口 = 排名列表的第一个
        var heads = new List<byte[]> { Solid(0), Solid(100), Solid(200) };
        var tails = new List<byte[]> { Solid(100), Solid(210) };
        var headPts = new List<double> { 0.0, 0.5, 1.0 };
        var tailPts = new List<double> { 9.0, 9.5 };

        var ranked = SeamAnalyzer.FindLoopPoints(heads, headPts, tails, tailPts);
        var first = SeamAnalyzer.FindLoopPoint(heads, headPts, tails, tailPts);

        Assert.Equal(ranked[0], first);
    }

    [Fact]
    public void FindLoopPoints_RanksAndDedupsByHeadSpacing()
    {
        // 四个同分命中候选，头帧 pts 0 / 2.5 / 5 / 5.5：间距过滤去掉 5.5（与 5 过近），
        // 保留前三个供轮换（时序项置零保证同分确定性）
        var heads = new List<byte[]> { Solid(10), Solid(20), Solid(10), Solid(30) };
        var tails = new List<byte[]> { Solid(10), Solid(20), Solid(30) };
        var headPts = new List<double> { 0.0, 2.5, 5.0, 5.5 };
        var tailPts = new List<double> { 10.0, 12.5, 13.0 };

        var ranked = SeamAnalyzer.FindLoopPoints(heads, headPts, tails, tailPts, temporalLookahead: 0);

        Assert.Equal([0, 1, 2], ranked.Select(p => p.HeadIndex).ToArray());
        Assert.All(ranked, p => Assert.False(p.Degraded));
    }

    [Fact]
    public void FindLoopPoints_TemporalTerm_SteersAwayFromDivergingMotion()
    {
        // 头帧 1 与尾帧自身更相似（0 差），但它的后续帧急速偏离尾窗后续——时序项必须把
        // 最优推回"自身稍差但运动趋势一致"的头帧 0（切口两侧速度反向最易看出跳变）
        var heads = new List<byte[]> { Solid(5), Solid(0), Solid(90) };
        var tails = new List<byte[]> { Solid(0), Solid(10) };
        var headPts = new List<double> { 0.0, 0.5, 1.0 };
        var tailPts = new List<double> { 10.0, 10.5 };

        var withoutTemporal = SeamAnalyzer.FindLoopPoints(
            heads, headPts, tails, tailPts, temporalLookahead: 0);
        var withTemporal = SeamAnalyzer.FindLoopPoints(heads, headPts, tails, tailPts);

        Assert.Equal(1, withoutTemporal[0].HeadIndex);
        Assert.Equal(0, withTemporal[0].HeadIndex);
    }

    [Fact]
    public void ShouldHardCut_ThresholdBoundary_IsExclusive()
    {
        Assert.True(SeamAnalyzer.ShouldHardCut(2.9));
        Assert.False(SeamAnalyzer.ShouldHardCut(SeamAnalyzer.HardCutThreshold));
        Assert.False(SeamAnalyzer.ShouldHardCut(SeamAnalyzer.SeamMatchThreshold));
        Assert.False(SeamAnalyzer.ShouldHardCut(50));
    }

    [Fact]
    public void PickCrossfadeDuration_MapsDifferenceToDuration()
    {
        // 硬切阈值以下不淡化；恰过阈值取下限；随差值线性增长到上限后封顶
        Assert.Equal(0, SeamAnalyzer.PickCrossfadeDuration(0));
        Assert.Equal(0, SeamAnalyzer.PickCrossfadeDuration(SeamAnalyzer.HardCutThreshold - 0.01));
        Assert.Equal(SeamAnalyzer.MinCrossfadeSeconds,
            SeamAnalyzer.PickCrossfadeDuration(SeamAnalyzer.HardCutThreshold), 5);
        Assert.Equal(SeamAnalyzer.MaxCrossfadeSeconds,
            SeamAnalyzer.PickCrossfadeDuration(SeamAnalyzer.FadeMaxDifference), 5);
        Assert.Equal(SeamAnalyzer.MaxCrossfadeSeconds,
            SeamAnalyzer.PickCrossfadeDuration(SeamAnalyzer.FadeMaxDifference * 4), 5);

        var mid = SeamAnalyzer.PickCrossfadeDuration(
            (SeamAnalyzer.HardCutThreshold + SeamAnalyzer.FadeMaxDifference) / 2);
        Assert.InRange(mid,
            SeamAnalyzer.MinCrossfadeSeconds, SeamAnalyzer.MaxCrossfadeSeconds);
        Assert.True(mid > SeamAnalyzer.MinCrossfadeSeconds);
        Assert.True(mid < SeamAnalyzer.MaxCrossfadeSeconds);
    }

    /// <summary>构造一幅恒定灰度的 RGB 缩略。</summary>
    private static byte[] Solid(byte value) => SolidRgb(value, value, value);

    /// <summary>构造一幅恒定颜色的 RGB 缩略。</summary>
    private static byte[] SolidRgb(byte r, byte g, byte b)
    {
        var thumb = new byte[SeamAnalyzer.ThumbWidth * SeamAnalyzer.ThumbHeight * SeamAnalyzer.ThumbChannels];
        for (var i = 0; i < SeamAnalyzer.ThumbWidth * SeamAnalyzer.ThumbHeight; i++)
        {
            thumb[i * 3] = r;
            thumb[i * 3 + 1] = g;
            thumb[i * 3 + 2] = b;
        }

        return thumb;
    }

    /// <summary>把左上分块（8×9 像素 = 8×4 网格的第一块）涂成指定值。</summary>
    private static void MarkBlock(byte[] thumb, byte value)
    {
        const int blockWidth = SeamAnalyzer.ThumbWidth / SeamAnalyzer.BlockColumns;
        const int blockHeight = SeamAnalyzer.ThumbHeight / SeamAnalyzer.BlockRows;
        for (var y = 0; y < blockHeight; y++)
        {
            for (var x = 0; x < blockWidth; x++)
            {
                var p = (y * SeamAnalyzer.ThumbWidth + x) * SeamAnalyzer.ThumbChannels;
                thumb[p] = value;
                thumb[p + 1] = value;
                thumb[p + 2] = value;
            }
        }
    }
}
