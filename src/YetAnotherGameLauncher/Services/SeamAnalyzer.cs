namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 循环接缝的纯托管分析核心：帧灰度缩略、帧对平均绝对差、循环点搜索与接缝切换策略。
/// 不含 FFmpeg 互操作（仅提供一个原生像素指针的缩略入口重载），逻辑整体可单测。
/// 播放器（<see cref="FfmpegVideoBackdropPlayer"/>）用它在头/尾窗口里找最相似的一对帧，
/// 把循环接缝落在两张几乎相同的画面之间，配合零间隙预卷做到循环无痕。
/// </summary>
internal static class SeamAnalyzer
{
    /// <summary>灰度缩略宽度（约 16:9；纵横比轻微失真对帧对比较无影响，两侧一致即可）。</summary>
    internal const int ThumbWidth = 64;

    /// <summary>灰度缩略高度。</summary>
    internal const int ThumbHeight = 36;

    /// <summary>循环点判定阈值：帧对平均绝对差（0-255 亮度）低于该值视为"几乎相同"。</summary>
    internal const double SeamMatchThreshold = 6.0;

    /// <summary>最小循环时长（秒）：首尾匹配帧过近会让循环过短产生频闪，这样的帧对直接拒绝。</summary>
    internal const double MinLoopSeconds = 1.0;

    /// <summary>循环点搜索命中结果。</summary>
    /// <param name="HeadIndex">头窗口帧下标（该帧即循环起点）。</param>
    /// <param name="TailIndex">尾窗口帧下标（该帧即循环终点）。</param>
    /// <param name="Difference">该帧对的平均绝对差（0-255）。</param>
    internal sealed record LoopPoint(int HeadIndex, int TailIndex, double Difference);

    /// <summary>BGRA 像素缩略为灰度图：盒式降采样 + ITU-R BT.601 亮度加权。</summary>
    /// <param name="bgra">源像素（每像素 4 字节，内存序 B,G,R,A）。</param>
    /// <param name="srcWidth">源宽（像素）。</param>
    /// <param name="srcHeight">源高（像素）。</param>
    /// <param name="srcStride">源行距（字节）。</param>
    /// <param name="dstWidth">目标宽（像素）。</param>
    /// <param name="dstHeight">目标高（像素）。</param>
    /// <returns>目标尺寸的灰度数组（每像素 1 字节，行优先）。</returns>
    internal static byte[] DownsampleBgraToGray(
        ReadOnlySpan<byte> bgra, int srcWidth, int srcHeight, int srcStride, int dstWidth, int dstHeight)
    {
        var gray = new byte[dstWidth * dstHeight];
        for (var y = 0; y < dstHeight; y++)
        {
            var y0 = y * srcHeight / dstHeight;
            var y1 = Math.Max(y0 + 1, (y + 1) * srcHeight / dstHeight);
            for (var x = 0; x < dstWidth; x++)
            {
                var x0 = x * srcWidth / dstWidth;
                var x1 = Math.Max(x0 + 1, (x + 1) * srcWidth / dstWidth);
                long sum = 0;
                long count = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * srcStride;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var offset = row + sx * 4;
                        sum += (bgra[offset + 2] * 299 + bgra[offset + 1] * 587 + bgra[offset] * 114) / 1000;
                        count++;
                    }
                }

                gray[y * dstWidth + x] = (byte)(count > 0 ? sum / count : 0);
            }
        }

        return gray;
    }

    /// <summary>原生像素缓冲重载：播放器的末帧 BGRA 暂存缓冲直接进分析。</summary>
    /// <param name="bgra">源像素指针（每像素 4 字节，内存序 B,G,R,A）。</param>
    /// <param name="srcWidth">源宽（像素）。</param>
    /// <param name="srcHeight">源高（像素）。</param>
    /// <param name="srcStride">源行距（字节）。</param>
    /// <param name="dstWidth">目标宽（像素）。</param>
    /// <param name="dstHeight">目标高（像素）。</param>
    /// <returns>目标尺寸的灰度数组（每像素 1 字节，行优先）。</returns>
    internal static unsafe byte[] DownsampleBgraToGray(
        byte* bgra, int srcWidth, int srcHeight, int srcStride, int dstWidth, int dstHeight)
        => DownsampleBgraToGray(
            new ReadOnlySpan<byte>(bgra, srcHeight * srcStride), srcWidth, srcHeight, srcStride, dstWidth, dstHeight);

    /// <summary>等尺寸灰度缩略的平均绝对差（0-255）；长度不一致视为调用方缺陷直接抛出。</summary>
    /// <param name="a">第一幅灰度缩略。</param>
    /// <param name="b">第二幅灰度缩略。</param>
    /// <returns>逐像素绝对差的平均值。</returns>
    internal static double MeanAbsoluteDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Gray thumbs must have equal length");
        }

        if (a.Length == 0)
        {
            return 0;
        }

        long total = 0;
        for (var i = 0; i < a.Length; i++)
        {
            total += Math.Abs(a[i] - b[i]);
        }

        return (double)total / a.Length;
    }

    /// <summary>
    /// 在头/尾两段灰度缩略序列里搜索最相似的帧对（接缝方向 = 尾帧 → 头帧）。
    /// 同分保留最早的头帧（循环更长）；首尾间距不足最小循环时长的帧对跳过；
    /// 没有低于阈值的命中返回 null（调用方回退为整段循环）。
    /// </summary>
    /// <param name="headThumbs">头窗口灰度缩略（按时间升序）。</param>
    /// <param name="headPtsSeconds">头窗口各帧呈现时刻（秒，与缩略一一对应）。</param>
    /// <param name="tailThumbs">尾窗口灰度缩略（按时间升序）。</param>
    /// <param name="tailPtsSeconds">尾窗口各帧呈现时刻（秒，与缩略一一对应）。</param>
    /// <param name="threshold">判定阈值（平均绝对差低于该值才算命中）。</param>
    /// <param name="minLoopSeconds">最小循环时长（秒）。</param>
    /// <returns>命中的循环点；无命中为 null。</returns>
    internal static LoopPoint? FindLoopPoint(
        IReadOnlyList<byte[]> headThumbs,
        IReadOnlyList<double> headPtsSeconds,
        IReadOnlyList<byte[]> tailThumbs,
        IReadOnlyList<double> tailPtsSeconds,
        double threshold = SeamMatchThreshold,
        double minLoopSeconds = MinLoopSeconds)
    {
        if (headThumbs.Count == 0 || tailThumbs.Count == 0)
        {
            return null;
        }

        LoopPoint? best = null;
        for (var head = 0; head < headThumbs.Count; head++)
        {
            for (var tail = 0; tail < tailThumbs.Count; tail++)
            {
                if (tailPtsSeconds[tail] - headPtsSeconds[head] < minLoopSeconds)
                {
                    continue;
                }

                var difference = MeanAbsoluteDifference(headThumbs[head], tailThumbs[tail]);
                if (difference > threshold)
                {
                    continue;
                }

                // 严格小于保证同分时最早的头帧胜出（头帧按下标升序遍历）
                if (best is null || difference < best.Difference)
                {
                    best = new LoopPoint(head, tail, difference);
                }
            }
        }

        return best;
    }

    /// <summary>接缝切换策略：差值低于阈值直接硬化切——内容几乎相同时，交叉淡化反而引入涂抹感。</summary>
    /// <param name="seamDifference">接缝两侧帧的平均绝对差。</param>
    /// <param name="threshold">判定阈值。</param>
    /// <returns>true = 硬化切（跳过淡化层）。</returns>
    internal static bool ShouldHardCut(double seamDifference, double threshold = SeamMatchThreshold)
        => seamDifference < threshold;
}
