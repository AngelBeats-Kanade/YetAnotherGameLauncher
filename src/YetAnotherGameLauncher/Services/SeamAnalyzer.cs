namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 循环接缝的纯托管分析核心：帧彩色（RGB）缩略、帧对综合评分（全局平均差 + 分块最大差惩罚 +
/// 时序连续性）、循环点搜索（排名候选 + 降级兜底）与接缝切换策略（硬切阈值 / 自适应淡化时长）。
/// 不含 FFmpeg 互操作（仅提供一个原生像素指针的缩略入口重载），逻辑整体可单测。
/// 播放器（<see cref="FfmpegVideoBackdropPlayer"/>）用它在头/尾窗口里找最相似的帧对，
/// 把循环接缝落在两张几乎相同的画面之间，配合零间隙预卷做到循环无痕。
/// v2（2026-09-21）：纯灰度会漏色偏、全局平均会淹没局部动作跳变、无命中时整段循环的接缝完全未分析
/// ——综合评分与降级兜底即为此而设；淡化时长随接缝差自适应（固定 0.6s 长溶解本身就是"循环信号"）。
/// </summary>
internal static class SeamAnalyzer
{
    /// <summary>缩略宽度（约 16:9；纵横比轻微失真对帧对比较无影响，两侧一致即可）。</summary>
    internal const int ThumbWidth = 64;

    /// <summary>缩略高度。</summary>
    internal const int ThumbHeight = 36;

    /// <summary>缩略通道数：RGB 交错（每像素 3 字节）。</summary>
    internal const int ThumbChannels = 3;

    /// <summary>循环点判定阈值：帧对综合分低于该值视为"几乎相同"。</summary>
    internal const double SeamMatchThreshold = 6.0;

    /// <summary>硬切阈值（严于命中阈值）：分值低于该值时内容几乎逐像素相同，直接切换——
    /// 高于它但低于命中阈值的轻微差异走短微淡化，避免溶解感出卖循环点。</summary>
    internal const double HardCutThreshold = 3.0;

    /// <summary>最小循环时长（秒）：首尾匹配帧过近会让循环过短产生频闪，这样的帧对直接拒绝。</summary>
    internal const double MinLoopSeconds = 1.0;

    /// <summary>分块最大差惩罚权重：全局平均会淹没局部跳变（主体挪了、背景没动），最差块均值按该权重计入综合分。</summary>
    internal const double BlockPenaltyWeight = 0.25;

    /// <summary>分块网格列数（64×36 → 每块 8×9：块太大稀释局部信号，太小对噪声敏感）。</summary>
    internal const int BlockColumns = 8;

    /// <summary>分块网格行数。</summary>
    internal const int BlockRows = 4;

    /// <summary>时序连续性回看帧数：候选对还要求接下来几帧也相似（运动方向一致），拒绝"姿势相似但速度相反"。</summary>
    internal const int TemporalLookaheadFrames = 2;

    /// <summary>时序连续性项权重。</summary>
    internal const double TemporalWeight = 0.3;

    /// <summary>排名候选保留数量（多循环点轮换：每过一圈换切点，重复周期翻倍且每圈路径不同）。</summary>
    internal const int MaxLoopPairs = 3;

    /// <summary>轮换候选之间的最小头帧间距（秒）：切点太近等于没换。</summary>
    internal const double PairHeadSpacingSeconds = 2.0;

    /// <summary>自适应淡化时长下限（秒）。</summary>
    internal const double MinCrossfadeSeconds = 0.15;

    /// <summary>自适应淡化时长上限（秒）。</summary>
    internal const double MaxCrossfadeSeconds = 0.7;

    /// <summary>淡化时长线性映射的差值上限：接缝差达到该值即封顶到上限时长。</summary>
    internal const double FadeMaxDifference = 18.0;

    /// <summary>循环点搜索命中结果。</summary>
    /// <param name="HeadIndex">头窗口帧下标（该帧即循环起点）。</param>
    /// <param name="TailIndex">尾窗口帧下标（该帧即循环终点，为最后一帧<b>渲染</b>帧——切口在其后）。</param>
    /// <param name="Difference">该帧对的综合分（全局平均 + 分块惩罚 + 时序项；0-255 量纲附近）。</param>
    /// <param name="Degraded">降级兜底命中：没有任何阈值内候选时取的全局最优，播放器侧须用最长淡化盖住接缝。</param>
    internal sealed record LoopPoint(int HeadIndex, int TailIndex, double Difference, bool Degraded = false);

    /// <summary>BGRA 像素缩略为 RGB 图：盒式降采样，三通道独立平均。</summary>
    /// <param name="bgra">源像素（每像素 4 字节，内存序 B,G,R,A）。</param>
    /// <param name="srcWidth">源宽（像素）。</param>
    /// <param name="srcHeight">源高（像素）。</param>
    /// <param name="srcStride">源行距（字节）。</param>
    /// <param name="dstWidth">目标宽（像素）。</param>
    /// <param name="dstHeight">目标高（像素）。</param>
    /// <returns>目标尺寸的 RGB 数组（每像素 3 字节行优先交错）。</returns>
    internal static byte[] DownsampleBgraToRgb(
        ReadOnlySpan<byte> bgra, int srcWidth, int srcHeight, int srcStride, int dstWidth, int dstHeight)
    {
        var rgb = new byte[dstWidth * dstHeight * ThumbChannels];
        for (var y = 0; y < dstHeight; y++)
        {
            var y0 = y * srcHeight / dstHeight;
            var y1 = Math.Max(y0 + 1, (y + 1) * srcHeight / dstHeight);
            for (var x = 0; x < dstWidth; x++)
            {
                var x0 = x * srcWidth / dstWidth;
                var x1 = Math.Max(x0 + 1, (x + 1) * srcWidth / dstWidth);
                long sumB = 0;
                long sumG = 0;
                long sumR = 0;
                long count = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * srcStride;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var offset = row + sx * 4;
                        sumB += bgra[offset];
                        sumG += bgra[offset + 1];
                        sumR += bgra[offset + 2];
                        count++;
                    }
                }

                if (count > 0)
                {
                    var dst = (y * dstWidth + x) * ThumbChannels;
                    rgb[dst] = (byte)(sumR / count);
                    rgb[dst + 1] = (byte)(sumG / count);
                    rgb[dst + 2] = (byte)(sumB / count);
                }
            }
        }

        return rgb;
    }

    /// <summary>原生像素缓冲重载：播放器的末帧 BGRA 暂存缓冲直接进分析。</summary>
    /// <param name="bgra">源像素指针（每像素 4 字节，内存序 B,G,R,A）。</param>
    /// <param name="srcWidth">源宽（像素）。</param>
    /// <param name="srcHeight">源高（像素）。</param>
    /// <param name="srcStride">源行距（字节）。</param>
    /// <param name="dstWidth">目标宽（像素）。</param>
    /// <param name="dstHeight">目标高（像素）。</param>
    /// <returns>目标尺寸的 RGB 数组（每像素 3 字节行优先交错）。</returns>
    internal static unsafe byte[] DownsampleBgraToRgb(
        byte* bgra, int srcWidth, int srcHeight, int srcStride, int dstWidth, int dstHeight)
        => DownsampleBgraToRgb(
            new ReadOnlySpan<byte>(bgra, srcHeight * srcStride), srcWidth, srcHeight, srcStride, dstWidth, dstHeight);

    /// <summary>等尺寸 RGB 缩略的帧对综合分：三通道平均绝对差的全局均值 + 最差分块均值的加权惩罚。
    /// 长度不一致视为调用方缺陷直接抛出。</summary>
    /// <param name="a">第一幅 RGB 缩略。</param>
    /// <param name="b">第二幅 RGB 缩略。</param>
    /// <returns>综合分（越低越相似）。</returns>
    internal static double Difference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("RGB thumbs must have equal length");
        }

        var pixels = a.Length / ThumbChannels;
        if (pixels == 0)
        {
            return 0;
        }

        long total = 0;
        var blockSums = new long[BlockColumns * BlockRows];
        var blockCounts = new long[BlockColumns * BlockRows];
        for (var i = 0; i < pixels; i++)
        {
            var p = i * ThumbChannels;
            var diff = (Math.Abs(a[p] - b[p]) + Math.Abs(a[p + 1] - b[p + 1]) + Math.Abs(a[p + 2] - b[p + 2])) / 3;
            total += diff;
            var block = i / ThumbWidth * BlockRows / ThumbHeight * BlockColumns
                + i % ThumbWidth * BlockColumns / ThumbWidth;
            blockSums[block] += diff;
            blockCounts[block]++;
        }

        var globalMean = (double)total / pixels;
        var maxBlockMean = 0.0;
        for (var i = 0; i < blockSums.Length; i++)
        {
            if (blockCounts[i] > 0)
            {
                maxBlockMean = Math.Max(maxBlockMean, (double)blockSums[i] / blockCounts[i]);
            }
        }

        return globalMean + BlockPenaltyWeight * maxBlockMean;
    }

    /// <summary>
    /// 在头/尾两段 RGB 缩略序列里搜索循环点<b>候选排名</b>（接缝方向 = 尾帧 → 头帧）。
    /// 每个候选的综合分含分块惩罚与时序连续性项；返回按分数升序、头帧间距去重后的前
    /// <paramref name="maxPairs"/> 个（供多循环点轮换）。没有任何阈值内命中时<b>不返回空</b>——
    /// 取全局最优并标记 <see cref="LoopPoint.Degraded"/>（降级兜底），杜绝"未分析的整段循环接缝"；
    /// 输入为空才返回空列表。
    /// </summary>
    /// <param name="headThumbs">头窗口 RGB 缩略（按时间升序）。</param>
    /// <param name="headPtsSeconds">头窗口各帧呈现时刻（秒，与缩略一一对应）。</param>
    /// <param name="tailThumbs">尾窗口 RGB 缩略（按时间升序）。</param>
    /// <param name="tailPtsSeconds">尾窗口各帧呈现时刻（秒，与缩略一一对应）。</param>
    /// <param name="threshold">判定阈值（综合分低于该值才算命中）。</param>
    /// <param name="minLoopSeconds">最小循环时长（秒）。</param>
    /// <param name="temporalLookahead">时序连续性回看帧数。</param>
    /// <param name="maxPairs">保留的候选数量。</param>
    /// <param name="pairHeadSpacingSeconds">候选之间的最小头帧间距（秒）。</param>
    /// <returns>排名候选（最好在前）；无任何可评估帧对时为空。</returns>
    internal static IReadOnlyList<LoopPoint> FindLoopPoints(
        IReadOnlyList<byte[]> headThumbs,
        IReadOnlyList<double> headPtsSeconds,
        IReadOnlyList<byte[]> tailThumbs,
        IReadOnlyList<double> tailPtsSeconds,
        double threshold = SeamMatchThreshold,
        double minLoopSeconds = MinLoopSeconds,
        int temporalLookahead = TemporalLookaheadFrames,
        int maxPairs = MaxLoopPairs,
        double pairHeadSpacingSeconds = PairHeadSpacingSeconds)
    {
        if (headThumbs.Count == 0 || tailThumbs.Count == 0)
        {
            return [];
        }

        // 头升序遍历 + 严格小于：同分时最早的头帧胜出（循环更长）——OrderBy 稳定排序保序
        LoopPoint? best = null;
        var candidates = new List<LoopPoint>();
        for (var head = 0; head < headThumbs.Count; head++)
        {
            for (var tail = 0; tail < tailThumbs.Count; tail++)
            {
                if (tailPtsSeconds[tail] - headPtsSeconds[head] < minLoopSeconds)
                {
                    continue;
                }

                var score = PairScore(headThumbs, tailThumbs, head, tail, temporalLookahead);
                if (best is null || score < best.Difference)
                {
                    best = new LoopPoint(head, tail, score);
                }

                if (score <= threshold)
                {
                    candidates.Add(new LoopPoint(head, tail, score));
                }
            }
        }

        if (best is null)
        {
            return [];
        }

        if (candidates.Count == 0)
        {
            // 降级兜底：接缝差可能偏大，但至少是全片可选范围内最优的一对——
            // 配自适应淡化（最长档）远好于未分析的"片尾→片头"原生接缝
            return [best with { Degraded = true }];
        }

        var ranked = candidates.OrderBy(c => c.Difference).ToList();
        var chosen = new List<LoopPoint>();
        foreach (var candidate in ranked)
        {
            if (chosen.Count >= maxPairs)
            {
                break;
            }

            var tooClose = false;
            foreach (var picked in chosen)
            {
                if (Math.Abs(headPtsSeconds[candidate.HeadIndex] - headPtsSeconds[picked.HeadIndex])
                    < pairHeadSpacingSeconds)
                {
                    tooClose = true;
                    break;
                }
            }

            if (!tooClose)
            {
                chosen.Add(candidate);
            }
        }

        // 头帧间距过滤不可能清空（排名首个必进），防御性兜底
        if (chosen.Count == 0)
        {
            chosen.Add(ranked[0]);
        }

        return chosen;
    }

    /// <summary>单候选便捷入口：排名中的第一个（无命中含降级兜底；无可评估帧对为 null）。</summary>
    internal static LoopPoint? FindLoopPoint(
        IReadOnlyList<byte[]> headThumbs,
        IReadOnlyList<double> headPtsSeconds,
        IReadOnlyList<byte[]> tailThumbs,
        IReadOnlyList<double> tailPtsSeconds,
        double threshold = SeamMatchThreshold,
        double minLoopSeconds = MinLoopSeconds,
        int temporalLookahead = TemporalLookaheadFrames)
        => FindLoopPoints(
            headThumbs, headPtsSeconds, tailThumbs, tailPtsSeconds,
            threshold, minLoopSeconds, temporalLookahead).FirstOrDefault();

    /// <summary>候选对的时序评分：帧对自身综合分 + 接下来 <paramref name="temporalLookahead"/> 帧平均分的加权
    /// （后续帧不在窗口内则按已有帧折算）——切口两侧的运动趋势一致才不容易看出跳变。</summary>
    private static double PairScore(
        IReadOnlyList<byte[]> headThumbs, IReadOnlyList<byte[]> tailThumbs,
        int head, int tail, int temporalLookahead)
    {
        var score = Difference(headThumbs[head], tailThumbs[tail]);
        if (temporalLookahead <= 0)
        {
            return score;
        }

        double lookaheadTotal = 0;
        var lookaheadCount = 0;
        for (var k = 1; k <= temporalLookahead; k++)
        {
            if (head + k >= headThumbs.Count || tail + k >= tailThumbs.Count)
            {
                break;
            }

            lookaheadTotal += Difference(headThumbs[head + k], tailThumbs[tail + k]);
            lookaheadCount++;
        }

        return lookaheadCount > 0 ? score + TemporalWeight * (lookaheadTotal / lookaheadCount) : score;
    }

    /// <summary>接缝切换策略：差值低于硬切阈值直接切换——内容几乎逐像素相同时，交叉淡化反而引入涂抹感。</summary>
    /// <param name="seamDifference">接缝两侧帧的综合分。</param>
    /// <param name="threshold">硬切阈值。</param>
    /// <returns>true = 硬切（跳过淡化层）。</returns>
    internal static bool ShouldHardCut(double seamDifference, double threshold = HardCutThreshold)
        => seamDifference < threshold;

    /// <summary>
    /// 接缝自适应淡化时长（秒）：硬切阈值以下返回 0（不淡化）；之上从 <see cref="MinCrossfadeSeconds"/>
    /// 随差值线性增长、到 <see cref="FadeMaxDifference"/> 封顶 <see cref="MaxCrossfadeSeconds"/>。
    /// 短微淡化掩盖轻微错配，长溶解只留给真正接不上的内容——固定 0.6s 溶解本身就是醒目的循环信号。
    /// </summary>
    /// <param name="seamDifference">接缝两侧帧的综合分。</param>
    /// <returns>淡化时长（秒）；0 = 硬切不淡化。</returns>
    internal static double PickCrossfadeDuration(double seamDifference)
    {
        if (seamDifference < HardCutThreshold)
        {
            return 0;
        }

        var t = (seamDifference - HardCutThreshold) / (FadeMaxDifference - HardCutThreshold);
        t = Math.Min(1, Math.Max(0, t));
        return MinCrossfadeSeconds + t * (MaxCrossfadeSeconds - MinCrossfadeSeconds);
    }
}
