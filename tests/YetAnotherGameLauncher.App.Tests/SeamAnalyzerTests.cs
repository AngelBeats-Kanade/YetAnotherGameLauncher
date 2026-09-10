using YetAnotherGameLauncher.Services;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 循环接缝分析核心：灰度缩略的亮度换算与降采样、帧对平均绝对差、
/// 循环点搜索（阈值/最小循环时长/同分取最早头帧）与硬化切策略边界。
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

        var gray = SeamAnalyzer.DownsampleBgraToGray(frame, 64, 36, 64 * 4, SeamAnalyzer.ThumbWidth, SeamAnalyzer.ThumbHeight);

        Assert.Equal(SeamAnalyzer.ThumbWidth * SeamAnalyzer.ThumbHeight, gray.Length);
        Assert.All(gray, g => Assert.Equal(255, g));
    }

    [Fact]
    public void Downsample_PureBlue_UsesBt601LumaWeights()
    {
        // 纯蓝（B=255）：亮度 = 255*114/1000 = 29
        var frame = SolidBgra(32, 18, 255, 0, 0);

        var gray = SeamAnalyzer.DownsampleBgraToGray(frame, 32, 18, 32 * 4, 8, 8);

        Assert.All(gray, g => Assert.Equal(29, g));
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

        var gray = SeamAnalyzer.DownsampleBgraToGray(frame, width, 8, width * 4, 16, 4);

        for (var y = 0; y < 4; y++)
        {
            for (var x = 1; x < 16; x++)
            {
                Assert.True(gray[y * 16 + x] >= gray[y * 16 + x - 1]);
            }
        }
    }

    [Fact]
    public void Mad_IdenticalThumbs_IsZero()
    {
        var a = new byte[] { 10, 20, 30 };

        Assert.Equal(0, SeamAnalyzer.MeanAbsoluteDifference(a, a));
    }

    [Fact]
    public void Mad_ConstantOffset_EqualsOffset()
    {
        var a = new byte[] { 10, 20, 30, 40 };
        var b = new byte[] { 20, 30, 40, 50 };

        Assert.Equal(10, SeamAnalyzer.MeanAbsoluteDifference(a, b));
    }

    [Fact]
    public void Mad_LengthMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => SeamAnalyzer.MeanAbsoluteDifference(new byte[] { 1 }, new byte[] { 1, 2 }));
    }

    [Fact]
    public void FindLoopPoint_PicksMatchingPair()
    {
        // 头帧 1 与尾帧 0 完全相同，其余帧彼此不同
        var heads = new List<byte[]> { Solid(0), Solid(100), Solid(200) };
        var tails = new List<byte[]> { Solid(100), Solid(210) };
        var headPts = new List<double> { 0.0, 0.5, 1.0 };
        var tailPts = new List<double> { 9.0, 9.5 };

        var match = SeamAnalyzer.FindLoopPoint(heads, headPts, tails, tailPts);

        Assert.NotNull(match);
        Assert.Equal(1, match.HeadIndex);
        Assert.Equal(0, match.TailIndex);
        Assert.Equal(0, match.Difference);
    }

    [Fact]
    public void FindLoopPoint_RejectsPairsShorterThanMinLoop()
    {
        // 唯一相同的帧对间距只有 0.5 秒（低于最小循环时长）→ 无命中
        var heads = new List<byte[]> { Solid(0), Solid(100) };
        var tails = new List<byte[]> { Solid(100) };
        var headPts = new List<double> { 0.0, 1.0 };
        var tailPts = new List<double> { 1.5 };

        Assert.Null(SeamAnalyzer.FindLoopPoint(heads, headPts, tails, tailPts));
    }

    [Fact]
    public void FindLoopPoint_NoMatchAboveThreshold_ReturnsNull()
    {
        // 所有帧对差异 100，阈值用默认 6 → 无命中
        var heads = new List<byte[]> { Solid(0), Solid(10) };
        var tails = new List<byte[]> { Solid(100) };
        var headPts = new List<double> { 0.0, 0.5 };
        var tailPts = new List<double> { 9.0 };

        Assert.Null(SeamAnalyzer.FindLoopPoint(heads, headPts, tails, tailPts));
    }

    [Fact]
    public void FindLoopPoint_TieBreaksToEarliestHead()
    {
        // 头帧 0 与头帧 1 都与尾帧完全相同：同分取最早头帧（循环更长）
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
    public void ShouldHardCut_ThresholdBoundary_IsExclusive()
    {
        Assert.True(SeamAnalyzer.ShouldHardCut(5.9));
        Assert.False(SeamAnalyzer.ShouldHardCut(SeamAnalyzer.SeamMatchThreshold));
        Assert.False(SeamAnalyzer.ShouldHardCut(50));
    }

    /// <summary>构造一幅恒定灰度的缩略。</summary>
    private static byte[] Solid(byte value)
    {
        var thumb = new byte[SeamAnalyzer.ThumbWidth * SeamAnalyzer.ThumbHeight];
        thumb.AsSpan().Fill(value);
        return thumb;
    }
}
