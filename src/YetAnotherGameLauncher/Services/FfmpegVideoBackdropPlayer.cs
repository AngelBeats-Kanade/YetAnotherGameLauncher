using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using static FFmpeg.AutoGen.ffmpeg;

namespace YetAnotherGameLauncher.Services;

[ExcludeFromCodeCoverage]
/// <summary>
/// 基于 FFmpeg 的背景视频播放器：后台线程循环解码（软解为基线，D3D11VA/VAAPI 硬解自动启用），
/// 帧经 swscale 转成 BGRA 后逐行拷进 WriteableBitmap，UI 线程节流触发 <see cref="FrameUpdated"/>
/// 重绘。静音（不解码音频轨）、分辨率 clamp ≤1080p。
/// 无缝循环 = 智能循环点（头尾窗口找最相似帧对，接缝落在几乎相同的画面之间）
/// + 预卷零间隙收编（临近结尾提前解码好下一循环开头几帧，接缝处直接换源续播，无停顿）；
/// 预卷未就绪时回退关键帧回卷 + 交叉淡化。
/// 解码管线与原生库获取（<see cref="FfmpegLibraryResolver"/>）解耦，可整体替换实现。
/// </summary>
public sealed class FfmpegVideoBackdropPlayer(
    FfmpegLibraryResolver libraryResolver,
    ILogger<FfmpegVideoBackdropPlayer>? logger = null) : IVideoBackdropPlayer, IDisposable
{
    /// <summary>帧通知合并阈值（只合并极短突发）：正常 PTS 节拍下每帧都送达 UI——
    /// 阈值若与视频帧间隔同频（30fps≈33ms），节拍抖动会把通知成对吞掉，画面呈 15fps 且不连贯。</summary>
    private static readonly TimeSpan NotifyInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>背景渲染尺寸上限：解码与 blit 都按此裁剪，超出部分纯浪费。</summary>
    private const int MaxWidth = 1920;

    /// <summary>渲染高度上限。</summary>
    private const int MaxHeight = 1080;

    /// <summary>swscale 双线性插值（FFmpeg 头文件的 SWS_BILINEAR 宏；AutoGen 未生成该常量）。</summary>
    private const int SwsBilinear = 2;

    /// <summary>距循环终点该源秒数内即启动预卷：预卷要在终点前完成打开、对齐与前若干帧解码。</summary>
    private const double PrerollTriggerSeconds = 2.0;

    /// <summary>预卷预解码帧数：收编后按节拍先消费这些帧，为就地续解吸收调度抖动。</summary>
    private const int PrerollFrames = 10;

    /// <summary>循环点分析窗口：头/尾各分析的源秒数。</summary>
    private const double AnalysisWindowSeconds = 2.0;

    /// <summary>时长低于该值（秒）不做循环点分析（窗口过窄没有搜索价值）。</summary>
    private const double MinAnalysisDuration = 2.0;

    /// <summary>分析窗口单侧最多采集的帧数（防御异常高帧率或坏时长导致内存膨胀）。</summary>
    private const int MaxAnalyzedFrames = 240;

    private readonly object _gate = new();

    /// <summary>当前帧位图（解码线程写、UI 渲染线程读，位图内部缓冲自身同步）。</summary>
    private WriteableBitmap? _frame;

    /// <summary>循环回卷的淡化层：上一循环的末帧位图，随新循环逐帧淡出后释放。</summary>
    private WriteableBitmap? _fadeFrame;

    /// <summary>淡化层当前不透明度。</summary>
    private double _fadeOpacity;

    /// <summary>每帧递减的淡化步长（按帧率与淡化时长折算）。</summary>
    private double _fadeStep;

    /// <summary>淡化时长（秒）：覆盖循环接缝的交叉淡化窗口（仅预卷未就绪或接缝差异大时启用）。</summary>
    private const double FadeSeconds = 0.6;

    /// <summary>当前播放的取消源与代际（旧代循环的输出一律丢弃，避免 Stop/Play 竞争）。</summary>
    private CancellationTokenSource? _cts;
    private int _generation;

    /// <inheritdoc/>
    public IImage? Frame
    {
        get
        {
            lock (_gate)
            {
                return _frame;
            }
        }
    }

    /// <inheritdoc/>
    public IImage? FadeFrame
    {
        get
        {
            lock (_gate)
            {
                return _fadeFrame;
            }
        }
    }

    /// <inheritdoc/>
    public double FadeOpacity
    {
        get
        {
            lock (_gate)
            {
                return _fadeOpacity;
            }
        }
    }

    /// <inheritdoc/>
    public event EventHandler? FrameUpdated;

    /// <inheritdoc/>
    public async Task<bool> PlayAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return false;
        }

        StopCore();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var generation = Interlocked.Increment(ref _generation);
        _cts = cts;

        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                if (!libraryResolver.EnsureReady(cts.Token))
                {
                    started.TrySetResult(false);
                    return;
                }

                RunLoop(videoPath, cts, generation);
                started.TrySetResult(true);
            }
            catch (Exception ex)
            {
                logger?.LogInformation(ex, "Video backdrop playback failed");
                started.TrySetResult(false);
            }
            finally
            {
                cts.Dispose();
            }
        }, CancellationToken.None);

        return await started.Task;
    }

    /// <inheritdoc/>
    public void Stop() => StopCore();

    /// <summary>取消当前解码循环并推进代际（旧循环的收尾清理自动失效）。</summary>
    private void StopCore()
    {
        Interlocked.Increment(ref _generation);
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            cts.Cancel();
        }
    }

    /// <summary>
    /// 解码主循环：打开 → 智能循环点分析 → 逐帧按 PTS 节拍上屏 → 循环终点零间隙收编预卷源
    /// （未就绪则回退关键帧回卷 + 交叉淡化）；退出时全部释放。
    /// </summary>
    private unsafe void RunLoop(string path, CancellationTokenSource cts, int generation)
    {
        DecodeSource? active = null;
        AVPacket* packet = null;
        AVFrame* frame = null;
        AVFrame* softwareFrame = null;
        SwsContext* scaler = null;
        byte* pixelBuffer = null;
        PrerollHandoff<PrerollPayload>? preroll = null;
        var pendingFrames = new List<nint>();
        var pendingFramePts = new List<double>();
        var token = cts.Token;
        try
        {
            active = OpenDecodeSource(path, softwareOnly: false);
            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            softwareFrame = ffmpeg.av_frame_alloc();
            var fps = active.Fps;
            var timeBase = active.TimeBase;
            var duration = active.DurationSeconds;

            // 智能循环点：头/尾窗口找最相似帧对，把接缝落在几乎相同的画面之间；
            // 时间基/帧率/时长不齐就整段循环，靠接缝自适应兜底。
            // 起播不 seek：从流头顺序读取、由 aligningToStart 在渲染前丢弃循环起点之前的帧
            // （带时间戳的 seek 在部分环境的新开 demuxer 上不可靠，顺序读包最稳妥）
            var (loopStartPts, loopEndPts) = timeBase > 0 && fps > 0 && duration >= MinAnalysisDuration
                ? AnalyzeLoopPoints(path, duration, token)
                : (0.0, 0.0);
            if (loopStartPts > 0)
            {
                logger?.LogInformation(
                    "Video loop points: start {Start:F2}s end {End:F2}s (duration {Duration:F2}s)",
                    loopStartPts, loopEndPts, duration);
            }
            else
            {
                logger?.LogDebug("Video loop point analysis found no match, looping full clip");
            }

            // PTS 节拍（解码多快播多快会呈数倍速快进）；pts 与帧率都拿不到时保持不节拍
            var clock = timeBase > 0 || fps > 0 ? new PlaybackClock() : null;
            var halfFrame = fps > 0 ? 0.5 / fps : 0.001;
            long frameIndex = 0;
            var aligningToStart = loopStartPts > 0;
            var loopEndReached = false;
            var prerollKicked = false;
            var lastRenderedWidth = 0;
            var lastRenderedHeight = 0;
            double lastRenderedPts = -1;
            var notify = new NotifyThrottle();
            var failures = new DecodeFailureLog(logger);

            // 呈现一帧软帧：起点对帧丢弃 → 循环终点截断 → PTS 节拍等待 → 上屏 → 预卷触发；
            // 等待期间取消直接返回（外层 while 检查 token 退出），到达循环终点置 loopEndReached
            unsafe void PresentFrame(AVFrame* softFrame, double ptsSeconds)
            {
                if (aligningToStart)
                {
                    if (!double.IsNaN(ptsSeconds) && ptsSeconds < loopStartPts - halfFrame)
                    {
                        return; // 起播从流头顺序读取，渲染前丢弃循环起点之前的帧
                    }

                    aligningToStart = false;
                }

                if (loopEndPts > 0 && !double.IsNaN(ptsSeconds) && ptsSeconds >= loopEndPts)
                {
                    loopEndReached = true;
                    return;
                }

                if (clock is not null && !double.IsNaN(ptsSeconds))
                {
                    var delayMs = clock.WaitDelayMs(ptsSeconds);
                    if (delayMs is > 0
                        && token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(delayMs.Value)))
                    {
                        return;
                    }
                }

                RenderFrame(softFrame, ref scaler, ref pixelBuffer, notify, failures);
                (lastRenderedWidth, lastRenderedHeight) = ClampEven(softFrame->width, softFrame->height);
                lastRenderedPts = double.IsNaN(ptsSeconds) ? lastRenderedPts : ptsSeconds;
                frameIndex++;
                TryKickPreroll(ptsSeconds);
            }

            // 距循环终点不足预卷窗口时启动一次预卷任务（每次循环至多一次）
            void TryKickPreroll(double ptsSeconds)
            {
                if (prerollKicked || double.IsNaN(ptsSeconds))
                {
                    return;
                }

                var loopEnd = loopEndPts > 0 ? loopEndPts : duration;
                if (loopEnd <= 0 || loopEnd - ptsSeconds > PrerollTriggerSeconds)
                {
                    return;
                }

                prerollKicked = true;
                var handoff = new PrerollHandoff<PrerollPayload>(FreePrerollPayload);
                preroll = handoff;
                logger?.LogDebug("Video preroll kicked at {Pts:F2}/{End:F2}s", ptsSeconds, loopEnd);
                _ = Task.Run(() => RunPreroll(path, loopStartPts, handoff, token), CancellationToken.None);
            }

            // 循环终点处理：预卷就绪则零间隙收编（按接缝差选硬化切/交叉淡化），
            // 否则回退关键帧回卷 + 交叉淡化。返回 false = 无法继续（应结束循环）
            bool HandleLoopEnd()
            {
                loopEndReached = false;
                if (preroll is not null && preroll.TryTake(out var payload))
                {
                    var seamDiff = ComputeSeamDiff(pixelBuffer, lastRenderedWidth, lastRenderedHeight, payload.FirstThumb);
                    var hardCut = SeamAnalyzer.ShouldHardCut(seamDiff);
                    logger?.LogInformation(
                        "Video loop seam: diff {Diff:F1} → {Mode}", seamDiff, hardCut ? "hard cut" : "crossfade");
                    if (!hardCut)
                    {
                        // 先把旧末帧转成淡化层，再让收编源的新帧上屏
                        PrepareLoopCrossfade(fps);
                    }

                    active.Dispose();
                    active = payload.Source;
                    pendingFrames = payload.Frames;
                    pendingFramePts = payload.PendingPts;
                    frameIndex = 0;
                    aligningToStart = false;
                    preroll = null;
                    prerollKicked = false;
                    clock?.Reset();
                    return true;
                }

                // 预卷未就绪：回退 = 重新打开全新解码源（不 seek——本构建的 mov seek 不可靠，
                // 顺序读取最稳妥）；打开停顿由交叉淡化掩盖
                preroll?.Abandon();
                preroll = null;
                prerollKicked = false;
                logger?.LogDebug(
                    "Video preroll not ready at loop end, reopening decoder instead (lastPts {Pts:F2}s, frameIndex {Index})",
                    lastRenderedPts, frameIndex);
                var reopened = OpenDecodeSource(path, softwareOnly: false);
                active.Dispose();
                active = reopened;
                pendingFrames.Clear();
                pendingFramePts.Clear();
                frameIndex = 0;
                aligningToStart = loopStartPts > 0;
                clock?.Reset();
                PrepareLoopCrossfade(fps);
                return true;
            }

            while (!token.IsCancellationRequested)
            {
                if (pendingFrames.Count > 0)
                {
                    // 收编的预解码帧优先消费：这是零间隙续播的关键路径
                    var pending = (AVFrame*)pendingFrames[0];
                    pendingFrames.RemoveAt(0);
                    var pendingPts = pendingFramePts[0];
                    pendingFramePts.RemoveAt(0);
                    PresentFrame(pending, pendingPts);
                    ffmpeg.av_frame_free(&pending);
                    if (loopEndReached && !HandleLoopEnd())
                    {
                        break;
                    }

                    continue;
                }

                if (!TryDecodeNextSoftFrame(active, packet, frame, softwareFrame, out var pts, failures))
                {
                    // 流结束（或不可恢复的读错误）：走循环终点处理
                    if (!HandleLoopEnd())
                    {
                        break;
                    }

                    continue;
                }

                // 无 pts 帧的呈现时刻推算：回退 帧序号/平均帧率；NaN = 无节拍信息
                if (double.IsNaN(pts) && fps > 0)
                {
                    pts = frameIndex / fps;
                }

                PresentFrame(softwareFrame, pts);
                ffmpeg.av_frame_unref(softwareFrame);
                if (loopEndReached && !HandleLoopEnd())
                {
                    break;
                }
            }
        }
        finally
        {
            preroll?.Abandon();
            FreeFrames(pendingFrames);
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (frame is not null)
            {
                ffmpeg.av_frame_free(&frame);
            }

            if (softwareFrame is not null)
            {
                ffmpeg.av_frame_free(&softwareFrame);
            }

            if (scaler is not null)
            {
                ffmpeg.sws_freeContext(scaler);
            }

            if (pixelBuffer is not null)
            {
                NativeMemory.AlignedFree(pixelBuffer);
            }

            active?.Dispose();

            // 循环结束时若已被新一代播放取代：帧位图归新一代所有，不在此清空
            if (Interlocked.CompareExchange(ref _generation, 0, 0) == generation)
            {
                ClearFrame();
            }
        }
    }

    /// <summary>
    /// 智能循环点分析：解码头/尾各约一个窗口的帧并采集灰度缩略，搜索最相似帧对。
    /// 任一步不可行返回 (0, 0)（整段循环），绝不抛出——起播不能因分析失败而失败。
    /// </summary>
    private unsafe (double LoopStart, double LoopEnd) AnalyzeLoopPoints(
        string path, double durationSeconds, CancellationToken token)
    {
        DecodeSource? source = null;
        AVPacket* packet = null;
        AVFrame* frame = null;
        AVFrame* softwareFrame = null;
        SwsContext* thumbScaler = null;
        try
        {
            source = OpenDecodeSource(path, softwareOnly: true);
            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            softwareFrame = ffmpeg.av_frame_alloc();

            var window = Math.Min(AnalysisWindowSeconds, durationSeconds / 3);
            var halfFrame = source.Fps > 0 ? 0.5 / source.Fps : 0.001;

            // 头窗口：从流开头解到 window 秒
            var headThumbs = new List<byte[]>();
            var headPts = new List<double>();
            CollectAnalyzedFrames(source, packet, frame, softwareFrame, ref thumbScaler,
                window + halfFrame, headThumbs, headPts, token);

            // 尾窗口：不 seek、不跳包（解码器需连续解码且起始包必须关键帧），继续顺序读到流结束，
            // 滚动保留最后 window 秒的帧即可
            var tailThumbs = new List<byte[]>();
            var tailPts = new List<double>();
            while (!token.IsCancellationRequested)
            {
                if (!TryDecodeNextSoftFrame(source, packet, frame, softwareFrame, out var pts))
                {
                    break; // 流结束
                }

                if (double.IsNaN(pts))
                {
                    ffmpeg.av_frame_unref(softwareFrame);
                    continue;
                }

                var thumb = ExtractGrayThumb(softwareFrame, ref thumbScaler);
                ffmpeg.av_frame_unref(softwareFrame);
                if (thumb is null)
                {
                    continue;
                }

                tailThumbs.Add(thumb);
                tailPts.Add(pts);
                while (tailPts.Count > 0 && tailPts[0] < pts - window)
                {
                    tailThumbs.RemoveAt(0);
                    tailPts.RemoveAt(0);
                }
            }

            // 只保留真正的尾窗区域（滚动过程中的中间帧不算），避免搜出过短的循环段
            while (tailPts.Count > 0 && tailPts[0] < durationSeconds - window - 0.05)
            {
                tailThumbs.RemoveAt(0);
                tailPts.RemoveAt(0);
            }

            if (token.IsCancellationRequested || headThumbs.Count == 0 || tailThumbs.Count == 0)
            {
                return (0, 0);
            }

            var match = SeamAnalyzer.FindLoopPoint(headThumbs, headPts, tailThumbs, tailPts);
            return match is null ? (0, 0) : (headPts[match.HeadIndex], tailPts[match.TailIndex]);
        }
        catch (Exception ex)
        {
            logger?.LogInformation(ex, "Video loop point analysis failed");
            return (0, 0);
        }
        finally
        {
            if (thumbScaler is not null)
            {
                ffmpeg.sws_freeContext(thumbScaler);
            }

            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (frame is not null)
            {
                ffmpeg.av_frame_free(&frame);
            }

            if (softwareFrame is not null)
            {
                ffmpeg.av_frame_free(&softwareFrame);
            }

            source?.Dispose();
        }
    }

    /// <summary>从当前位置连续解码软帧并采集灰度缩略，直到呈现时刻超过上限、流结束、取消或达到帧数上限。</summary>
    private static unsafe void CollectAnalyzedFrames(
        DecodeSource source,
        AVPacket* packet,
        AVFrame* frame,
        AVFrame* softwareFrame,
        ref SwsContext* thumbScaler,
        double untilSeconds,
        List<byte[]> thumbs,
        List<double> ptsSeconds,
        CancellationToken token)
    {
        while (thumbs.Count < MaxAnalyzedFrames && !token.IsCancellationRequested)
        {
            if (!TryDecodeNextSoftFrame(source, packet, frame, softwareFrame, out var pts))
            {
                return;
            }

            if (double.IsNaN(pts))
            {
                // 无 pts 的帧无法参与循环点定位：跳过
                ffmpeg.av_frame_unref(softwareFrame);
                continue;
            }

            if (pts > untilSeconds)
            {
                ffmpeg.av_frame_unref(softwareFrame);
                return;
            }

            var thumb = ExtractGrayThumb(softwareFrame, ref thumbScaler);
            if (thumb is not null)
            {
                thumbs.Add(thumb);
                ptsSeconds.Add(pts);
            }

            ffmpeg.av_frame_unref(softwareFrame);
        }
    }

    /// <summary>
    /// 预卷任务：打开独立解码源，对齐循环起点后预解码前若干帧为软件帧，经交接状态机交付。
    /// 取消或失败时不完成交接，资源在 finally 就地释放；交接已放弃时负载由状态机回调清理。
    /// </summary>
    private unsafe void RunPreroll(
        string path, double loopStartPts, PrerollHandoff<PrerollPayload> handoff, CancellationToken token)
    {
        DecodeSource? source = null;
        List<nint>? frames = null;
        AVPacket* packet = null;
        AVFrame* frame = null;
        AVFrame* softwareFrame = null;
        SwsContext* thumbScaler = null;
        try
        {
            source = OpenDecodeSource(path, softwareOnly: false);
            // 不 seek：新开的 demuxer 上带时间戳的 seek 不可靠，靠下方 pts 丢弃对齐循环起点
            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            softwareFrame = ffmpeg.av_frame_alloc();
            frames = [];
            var pendingPts = new List<double>();
            var halfFrame = source.Fps > 0 ? 0.5 / source.Fps : 0.001;
            byte[]? firstThumb = null;
            while (frames.Count < PrerollFrames && !token.IsCancellationRequested)
            {
                if (!TryDecodeNextSoftFrame(source, packet, frame, softwareFrame, out var pts))
                {
                    break;
                }

                // 精确对帧：丢弃循环起点之前的帧
                if (loopStartPts > 0 && !double.IsNaN(pts) && pts < loopStartPts - halfFrame)
                {
                    ffmpeg.av_frame_unref(softwareFrame);
                    continue;
                }

                var copy = ffmpeg.av_frame_alloc();
                if (ffmpeg.av_frame_ref(copy, softwareFrame) == 0)
                {
                    frames.Add((nint)copy);
                    pendingPts.Add(pts);
                    firstThumb ??= ExtractGrayThumb(softwareFrame, ref thumbScaler);
                }

                ffmpeg.av_frame_unref(softwareFrame);
            }

            if (token.IsCancellationRequested || frames.Count == 0 || firstThumb is null)
            {
                return; // 资源在 finally 就地释放，交接保持未完成
            }

            var payload = new PrerollPayload(source, frames, pendingPts, firstThumb);
            _ = handoff.TryComplete(payload);
            // TryComplete 返回 true = 所有权移交状态机；返回 false = 负载已被 Abandon 路径清理：
            // 两条路都不得再触碰，统一就地置空
            source = null;
            frames = null;
        }
        catch (Exception ex)
        {
            logger?.LogInformation(ex, "Video preroll decode failed");
        }
        finally
        {
            if (frames is not null)
            {
                FreeFrames(frames);
            }

            source?.Dispose();
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (frame is not null)
            {
                ffmpeg.av_frame_free(&frame);
            }

            if (softwareFrame is not null)
            {
                ffmpeg.av_frame_free(&softwareFrame);
            }

            if (thumbScaler is not null)
            {
                ffmpeg.sws_freeContext(thumbScaler);
            }
        }
    }

    /// <summary>打开一路解码源（输入 + 流元数据 + 解码器）；失败时释放已创建资源后原样抛出。</summary>
    private unsafe DecodeSource OpenDecodeSource(string path, bool softwareOnly)
    {
        var formatContext = OpenInput(path);
        try
        {
            var streamIndex = ffmpeg.av_find_best_stream(
                formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (streamIndex < 0)
            {
                throw new InvalidOperationException($"Video has no video stream: {path}");
            }

            var stream = formatContext->streams[streamIndex];
            var timeBase = ffmpeg.av_q2d(stream->time_base);
            var fps = ffmpeg.av_q2d(stream->avg_frame_rate);
            if (fps <= 0)
            {
                fps = ffmpeg.av_q2d(stream->r_frame_rate);
            }

            // 时长：容器时长优先，回退流时长 × 时间基；都拿不到记 0（不做分析与预卷）
            var duration = formatContext->duration != AV_NOPTS_VALUE && formatContext->duration > 0
                ? formatContext->duration / (double)AV_TIME_BASE
                : stream->duration > 0 && timeBase > 0 ? stream->duration * timeBase : 0;
            AVBufferRef* hwDevice = null;
            var codecContext = OpenDecoder(stream->codecpar, ref hwDevice, softwareOnly);
            return new DecodeSource(formatContext, codecContext, hwDevice, streamIndex, timeBase, fps, duration);
        }
        catch
        {
            ffmpeg.avformat_close_input(&formatContext);
            throw;
        }
    }

    /// <summary>打开输入文件（路径按 UTF-8 编组）。</summary>
    private unsafe AVFormatContext* OpenInput(string path)
    {
        AVFormatContext* context = null;
        if (ffmpeg.avformat_open_input(&context, path, null, null) != 0)
        {
            throw new InvalidOperationException($"Cannot open video: {path}");
        }

        return context;
    }

    /// <summary>打开解码器：按平台顺序试硬解（Windows D3D11VA / Linux VAAPI→NVDEC），全部创建失败回软解；硬解设备引用经 <paramref name="device"/> 返回。</summary>
    private unsafe AVCodecContext* OpenDecoder(AVCodecParameters* parameters, ref AVBufferRef* device, bool softwareOnly = false)
    {
        var decoder = ffmpeg.avcodec_find_decoder(parameters->codec_id);
        if (decoder is null)
        {
            throw new InvalidOperationException("No decoder for codec");
        }

        var context = ffmpeg.avcodec_alloc_context3(decoder);
        ffmpeg.avcodec_parameters_to_context(context, parameters);

        // Linux 按序尝试：VAAPI 覆盖 AMD/Intel（Mesa），创建失败（如 NVIDIA 专有驱动无 VAAPI）
        // 再试 CUDA（NVDEC）——设备创建本身就是探测，失败自动落到下一项
        var hwTypes = softwareOnly ? ReadOnlySpan<AVHWDeviceType>.Empty
            : OperatingSystem.IsWindows()
                ? [AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA]
                : OperatingSystem.IsLinux()
                    ? [AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA]
                    : ReadOnlySpan<AVHWDeviceType>.Empty;
        AVBufferRef* created = null;
        foreach (var hwType in hwTypes)
        {
            if (ffmpeg.av_hwdevice_ctx_create(&created, hwType, null, null, 0) == 0)
            {
                context->hw_device_ctx = ffmpeg.av_buffer_ref(created);
                device = created;
                logger?.LogDebug("Video backdrop hardware decode: {Type}", hwType);
                break;
            }

            created = null;
        }

        if (context->hw_device_ctx is null)
        {
            logger?.LogDebug("Video backdrop hardware decode unavailable, falling back to software");
        }

        if (ffmpeg.avcodec_open2(context, decoder, null) < 0)
        {
            ffmpeg.avcodec_free_context(&context);
            throw new InvalidOperationException("Cannot open video decoder");
        }

        return context;
    }

    /// <summary>读取并解码下一帧软帧：硬解输出回读系统内存，软解转移引用。<paramref name="ptsSeconds"/>
    /// 取自原始解码帧（硬解回读不拷贝时间戳属性，必须在回读前捕获）；返回 false = 流结束或无法继续。</summary>
    private static unsafe bool TryDecodeNextSoftFrame(
        DecodeSource source,
        AVPacket* packet,
        AVFrame* frame,
        AVFrame* softwareFrame,
        out double ptsSeconds,
        DecodeFailureLog? failures = null)
    {
        ptsSeconds = double.NaN;
        while (true)
        {
            var readResult = ffmpeg.av_read_frame(source.FormatContext, packet);
            if (readResult < 0)
            {
                if (readResult != AVERROR_EOF)
                {
                    failures?.Log("demuxer read error {Code}", readResult);
                }

                return false;
            }

            if (packet->stream_index != source.StreamIndex)
            {
                // 音频/字幕/封面等其他流：直接跳过（背景视频不解码音轨）
                ffmpeg.av_packet_unref(packet);
                continue;
            }

            var sent = ffmpeg.avcodec_send_packet(source.CodecContext, packet) >= 0;
            ffmpeg.av_packet_unref(packet);
            if (!sent)
            {
                failures?.Log("packet rejected by decoder (stream {Stream})", source.StreamIndex);
                continue;
            }

            if (ffmpeg.avcodec_receive_frame(source.CodecContext, frame) < 0)
            {
                // 解码器内部缓冲未出帧（EAGAIN）：继续喂数据
                continue;
            }

            // 原始帧的时间戳由解码器写入；回读/转移后再读会丢失
            var capturedPts = BestEffortPts(frame, source.TimeBase);
            var ok = true;
            if (frame->hw_frames_ctx is not null)
            {
                // 硬解输出的是 GPU 帧：回读到系统内存再进统一管线（PCIe 回读开销极小）
                ok = ffmpeg.av_hwframe_transfer_data(softwareFrame, frame, 0) >= 0;
                if (!ok)
                {
                    failures?.Log("hw frame transfer failed");
                }
            }
            else
            {
                // 软解：把解码帧引用转移到 softwareFrame，统一后续管线
                ok = ffmpeg.av_frame_ref(softwareFrame, frame) == 0;
            }

            ffmpeg.av_frame_unref(frame);
            ptsSeconds = ok ? capturedPts : double.NaN;
            return ok;
        }
    }

    /// <summary>帧呈现时刻（秒）：优先 best_effort_timestamp，缺失回退 pts；无时间基或无有效值返回 NaN。</summary>
    private static unsafe double BestEffortPts(AVFrame* frame, double timeBase)
    {
        if (timeBase <= 0)
        {
            return double.NaN;
        }

        var pts = frame->best_effort_timestamp;
        if (pts == AV_NOPTS_VALUE || pts < 0)
        {
            pts = frame->pts;
        }

        return pts != AV_NOPTS_VALUE && pts >= 0 ? pts * timeBase : double.NaN;
    }

    /// <summary>把软帧缩略成固定尺寸灰度图（分析用小尺寸 sws，缩略上下文按源格式缓存复用）；失败返回 null。</summary>
    private static unsafe byte[]? ExtractGrayThumb(AVFrame* softFrame, ref SwsContext* thumbScaler)
    {
        if (softFrame->width <= 0 || softFrame->height <= 0)
        {
            return null;
        }

        thumbScaler = ffmpeg.sws_getCachedContext(
            thumbScaler, softFrame->width, softFrame->height, (AVPixelFormat)softFrame->format,
            SeamAnalyzer.ThumbWidth, SeamAnalyzer.ThumbHeight, AVPixelFormat.AV_PIX_FMT_GRAY8,
            SwsBilinear, null, null, null);
        if (thumbScaler is null)
        {
            return null;
        }

        var thumb = new byte[SeamAnalyzer.ThumbWidth * SeamAnalyzer.ThumbHeight];
        fixed (byte* thumbPtr = thumb)
        {
            var destination = new byte_ptrArray4 { [0] = thumbPtr };
            var destinationLines = new int_array4 { [0] = SeamAnalyzer.ThumbWidth };
            ffmpeg.sws_scale(thumbScaler, softFrame->data, softFrame->linesize, 0, softFrame->height,
                destination, destinationLines);
        }

        return thumb;
    }

    /// <summary>接缝差：旧循环末帧（仍在像素暂存缓冲里）与预卷首帧缩略的平均绝对差；无末帧视为最大（必然走淡化）。</summary>
    private static unsafe double ComputeSeamDiff(byte* pixelBuffer, int width, int height, byte[] prerollFirstThumb)
    {
        if (pixelBuffer is null || width <= 0 || height <= 0)
        {
            return double.MaxValue;
        }

        var lastThumb = SeamAnalyzer.DownsampleBgraToGray(
            pixelBuffer, width, height, width * 4, SeamAnalyzer.ThumbWidth, SeamAnalyzer.ThumbHeight);
        return SeamAnalyzer.MeanAbsoluteDifference(lastThumb, prerollFirstThumb);
    }

    /// <summary>释放帧引用队列（每帧 av_frame_free，队列随之清空）。</summary>
    private static unsafe void FreeFrames(List<nint> frames)
    {
        foreach (var framePtr in frames)
        {
            var pointer = (AVFrame*)framePtr;
            if (pointer is not null)
            {
                ffmpeg.av_frame_free(&pointer);
            }
        }

        frames.Clear();
    }

    /// <summary>预卷负载的清理回调：释放预解码帧与解码源（交接状态机在恰当时机调用恰好一次）。</summary>
    private static unsafe void FreePrerollPayload(PrerollPayload payload)
    {
        FreeFrames(payload.Frames);
        payload.Source.Dispose();
    }

    /// <summary>
    /// 循环回卷的交叉淡化准备：旧循环末帧保留为淡化层（整体淡出掩盖接缝），
    /// 新循环写全新位图不受旧层覆盖；淡化步长按帧率折算（约 <see cref="FadeSeconds"/> 秒淡完）。
    /// </summary>
    private void PrepareLoopCrossfade(double fps)
    {
        lock (_gate)
        {
            if (_frame is null)
            {
                return;
            }

            _fadeFrame = _frame;
            _fadeStep = fps > 0 ? 1.0 / (fps * FadeSeconds) : 0.25;
            _fadeOpacity = 1;
            _frame = null;
        }
    }

    /// <summary>每渲染一帧推进一次淡化；归零后释放淡化层。</summary>
    private void AdvanceLoopCrossfade()
    {
        lock (_gate)
        {
            if (_fadeFrame is null)
            {
                return;
            }

            _fadeOpacity = Math.Max(0, _fadeOpacity - _fadeStep);
            if (_fadeOpacity <= 0)
            {
                _fadeFrame = null;
            }
        }
    }

    /// <summary>单帧处理：确保缩放器与缓冲匹配源格式 → swscale 到 BGRA → blit 进位图 → 节流通知。</summary>
    private unsafe void RenderFrame(
        AVFrame* source,
        ref SwsContext* scaler,
        ref byte* pixelBuffer,
        NotifyThrottle notify,
        DecodeFailureLog failures)
    {
        var (width, height) = ClampEven(source->width, source->height);
        if (width <= 0 || height <= 0)
        {
            failures.Log("invalid frame size {Width}x{Height}", source->width, source->height);
            return;
        }

        // 源格式/尺寸变化（硬解回读后的像素格式与软解不同）时重建缩放器
        scaler = ffmpeg.sws_getCachedContext(
            scaler, source->width, source->height, (AVPixelFormat)source->format,
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA, SwsBilinear, null, null, null);
        if (scaler is null)
        {
            failures.Log("cannot create swscale context");
            return;
        }

        var stride = width * 4;
        if (pixelBuffer is null)
        {
            pixelBuffer = (byte*)NativeMemory.AlignedAlloc((nuint)(stride * height), 64);
        }

        var destination = new byte_ptrArray4 { [0] = pixelBuffer };
        var destinationLines = new int_array4 { [0] = stride };
        ffmpeg.sws_scale(scaler, source->data, source->linesize, 0, source->height,
            destination, destinationLines);

        var bitmap = EnsureFrame(width, height);
        if (bitmap is null)
        {
            return;
        }

        // 解码线程逐行拷入位图（Avalonia 位图缓冲按行可能有对齐间距）
        using var locked = bitmap.Lock();
        var target = (byte*)locked.Address;
        for (var y = 0; y < height; y++)
        {
            Buffer.MemoryCopy(pixelBuffer + (nint)y * stride, target + (nint)y * locked.RowBytes, locked.RowBytes, stride);
        }

        AdvanceLoopCrossfade();
        notify.Post(NotifyFrame);
    }

    /// <summary>确保当前帧位图与视频尺寸一致（首次创建 / 尺寸变化重建）；创建失败返回 null。</summary>
    private WriteableBitmap? EnsureFrame(int width, int height)
    {
        lock (_gate)
        {
            if (_frame is { } existing
                && existing.PixelSize.Width == width && existing.PixelSize.Height == height)
            {
                return existing;
            }

            try
            {
                _frame = new WriteableBitmap(
                    new PixelSize(width, height), new Vector(96, 96),
                    global::Avalonia.Platform.PixelFormats.Bgra8888, global::Avalonia.Platform.AlphaFormat.Opaque);
                return _frame;
            }
            catch (Exception ex)
            {
                logger?.LogInformation(ex, "Cannot create frame bitmap");
                return null;
            }
        }
    }

    /// <summary>清空帧位图并通知渲染层（播放结束/被停止）。</summary>
    private void ClearFrame()
    {
        lock (_gate)
        {
            _frame = null;
            _fadeFrame = null;
            _fadeOpacity = 0;
        }

        NotifyFrame();
    }

    /// <summary>在 UI 线程触发帧就绪通知（渲染控件据此重绘）。</summary>
    private void NotifyFrame()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            FrameUpdated?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _ = Dispatcher.UIThread.InvokeAsync(
                () => FrameUpdated?.Invoke(this, EventArgs.Empty), DispatcherPriority.Background);
        }
    }

    /// <summary>把渲染尺寸 clamp 到上限并偶数化（多数编码器要求偶数尺寸）。</summary>
    private static (int Width, int Height) ClampEven(int width, int height)
    {
        var scale = Math.Min(1.0, Math.Min((double)MaxWidth / width, (double)MaxHeight / height));
        return (Math.Max(2, (int)(width * scale) & ~1), Math.Max(2, (int)(height * scale) & ~1));
    }

    /// <summary>解码失败的限频日志：同类失败只记首条与计数，避免坏文件刷爆日志。</summary>
    private sealed class DecodeFailureLog
    {
        private readonly ILogger? _logger;
        private readonly Dictionary<string, (int Count, object[] Args)> _seen = [];

        public DecodeFailureLog(ILogger? logger) => _logger = logger;

        /// <summary>记录失败：同模板首条立即输出，之后每 100 次再输出一次并带累计次数。</summary>
        public void Log(string template, params object[] args)
        {
            var (count, _) = _seen.TryGetValue(template, out var seen) ? seen : (0, args);
            count++;
            _seen[template] = (count, args);
            if (count == 1 || count % 100 == 0)
            {
                _logger?.LogInformation(
                    "video decode failure #{Count}: " + template, [count, .. args]);
            }
        }
    }

    /// <summary>帧通知节流器：背景不需要满帧率重绘，按最小间隔合并通知。</summary>
    private sealed class NotifyThrottle
    {
        private long _lastTicks;

        /// <summary>距上次通知超过最小间隔才投递一次。</summary>
        public void Post(Action notify)
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastTicks) < NotifyInterval.TotalMilliseconds)
            {
                return;
            }

            Interlocked.Exchange(ref _lastTicks, now);
            notify();
        }
    }

    [ExcludeFromCodeCoverage]
    /// <summary>一路打开的视频解码源（输入 + 解码器 + 硬解设备）与流元数据；循环接缝处整体收编替换。</summary>
    private sealed unsafe class DecodeSource : IDisposable
    {
        /// <summary>输入格式上下文。</summary>
        public AVFormatContext* FormatContext;

        /// <summary>视频解码器上下文。</summary>
        public AVCodecContext* CodecContext;

        /// <summary>硬解设备引用（软解为 null）。</summary>
        public AVBufferRef* HwDevice;

        /// <summary>视频流下标。</summary>
        public readonly int StreamIndex;

        /// <summary>流时间基（秒）。</summary>
        public readonly double TimeBase;

        /// <summary>平均帧率（fps）。</summary>
        public readonly double Fps;

        /// <summary>容器时长（秒，拿不到为 0）。</summary>
        public readonly double DurationSeconds;

        /// <summary>组装解码源。</summary>
        public DecodeSource(
            AVFormatContext* formatContext,
            AVCodecContext* codecContext,
            AVBufferRef* hwDevice,
            int streamIndex,
            double timeBase,
            double fps,
            double durationSeconds)
        {
            FormatContext = formatContext;
            CodecContext = codecContext;
            HwDevice = hwDevice;
            StreamIndex = streamIndex;
            TimeBase = timeBase;
            Fps = fps;
            DurationSeconds = durationSeconds;
        }

        /// <summary>按依赖逆序释放全部原生资源（各指针释放后置 null，可安全重复调用）。</summary>
        public void Dispose()
        {
            var codecContext = CodecContext;
            if (codecContext is not null)
            {
                CodecContext = null;
                ffmpeg.avcodec_free_context(&codecContext);
            }

            var hwDevice = HwDevice;
            if (hwDevice is not null)
            {
                HwDevice = null;
                ffmpeg.av_buffer_unref(&hwDevice);
            }

            var formatContext = FormatContext;
            if (formatContext is not null)
            {
                FormatContext = null;
                ffmpeg.avformat_close_input(&formatContext);
            }
        }
    }

    [ExcludeFromCodeCoverage]
    /// <summary>预卷负载：已对齐循环起点的解码源 + 预解码的软件帧队列（含捕获的 pts）+ 首帧灰度缩略。</summary>
    private sealed unsafe class PrerollPayload(DecodeSource source, List<nint> frames, List<double> pendingPts, byte[] firstThumb)
    {
        /// <summary>已打开并对齐到循环起点的解码源（含硬解设备）。</summary>
        public readonly DecodeSource Source = source;

        /// <summary>预解码的软件帧指针（nint 存放 AVFrame*，引用计数持有，按呈现顺序排列）。</summary>
        public readonly List<nint> Frames = frames;

        /// <summary>与 <see cref="Frames"/> 一一对应的呈现时刻（秒，硬解回读会丢属性所以提前捕获）。</summary>
        public readonly List<double> PendingPts = pendingPts;

        /// <summary>首帧的灰度缩略（接缝差计算用）。</summary>
        public readonly byte[] FirstThumb = firstThumb;
    }

    /// <inheritdoc/>
    public void Dispose() => StopCore();
}

/// <summary>
/// PTS 实时节拍器：把帧呈现时刻（秒）映射到单调时钟，让视频按片源原生速度播放。
/// 首帧立即渲染；大幅落后（EOF 回卷后 pts 归零、解码卡顿）时重定基线直接渲染，不做爆发追帧。
/// internal 供单测（经 InternalsVisibleTo）。
/// </summary>
internal sealed class PlaybackClock
{
    /// <summary>落后超过该值（毫秒）即重定基线（回卷/卡顿后直接恢复，不爆发追赶）。</summary>
    internal const double RebaseThresholdMs = 250;

    private readonly Stopwatch _clock = new();

    /// <summary>播放速率（1.0 = 片源原生速度；预留给未来调速，当前恒为原生）。</summary>
    private readonly double _rate;

    private double? _basePts;

    private double _baseElapsedMs;

    public PlaybackClock(double rate = 1.0) => _rate = rate > 0 ? rate : 1.0;

    /// <summary>重置时钟（开始播放或 EOF 回卷后调用，下帧重新取基线）。</summary>
    public void Reset() => _basePts = null;

    /// <summary>
    /// 计算当前帧距离目标呈现时刻的等待毫秒数：null = 立即渲染（首帧/重定基线）；
    /// 0 = 时刻已到；正数 = 还需等待的毫秒（调用方以可取消等待消化）。
    /// </summary>
    public double? WaitDelayMs(double ptsSeconds)
    {
        if (!_clock.IsRunning)
        {
            _clock.Start();
        }

        if (_basePts is not { } basePts)
        {
            _basePts = ptsSeconds;
            _baseElapsedMs = _clock.Elapsed.TotalMilliseconds;
            return null;
        }

        var targetMs = _baseElapsedMs + (ptsSeconds - basePts) * 1000 / _rate;
        var delayMs = targetMs - _clock.Elapsed.TotalMilliseconds;
        if (delayMs < -RebaseThresholdMs)
        {
            _basePts = ptsSeconds;
            _baseElapsedMs = _clock.Elapsed.TotalMilliseconds;
            return null;
        }

        return Math.Max(0, delayMs);
    }
}
