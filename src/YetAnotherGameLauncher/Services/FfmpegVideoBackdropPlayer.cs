using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using static FFmpeg.AutoGen.ffmpeg;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 基于 FFmpeg 的背景视频播放器：后台线程循环解码（软解为基线，D3D11VA/VAAPI 硬解自动启用），
/// 帧经 swscale 转成 BGRA 后逐行拷进 WriteableBitmap，UI 线程节流触发 <see cref="FrameUpdated"/>
/// 重绘。静音（不解码音频轨）、分辨率 clamp ≤4K（防呆上限，官方投放原样渲染不降采样）。
/// 无缝循环 = 智能循环点（头尾窗口找最相似帧对，接缝落在几乎相同的画面之间；v2：RGB 综合评分、
/// 渲染选中尾帧的精确切口、多循环点轮换与自适应淡化时长）
/// + 预卷零间隙收编（临近结尾提前解码好下一循环开头几帧，接缝处直接换源续播，无停顿）；
/// 预卷未就绪时回退重开解码源 + 最长交叉淡化。
/// 解码管线与原生库获取（<see cref="FfmpegLibraryResolver"/>）解耦，可整体替换实现。
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class FfmpegVideoBackdropPlayer(
    FfmpegLibraryResolver libraryResolver,
    ILogger<FfmpegVideoBackdropPlayer>? logger = null) : IVideoBackdropPlayer, IDisposable
{
    /// <summary>帧通知合并阈值（只合并极短突发）：正常 PTS 节拍下每帧都送达 UI——
    /// 阈值若与视频帧间隔同频（30fps≈33ms），节拍抖动会把通知成对吞掉，画面呈 15fps 且不连贯。</summary>
    private static readonly TimeSpan NotifyInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>背景渲染尺寸防呆上限：官方投放（2026-09 实测最高 2324×1392）原样渲染不降采样，
    /// 仅拦截异常超大源；解码本就按源分辨率全量进行，此 clamp 只作用于 swscale 输出目标。</summary>
    private const int MaxWidth = 3840;

    /// <summary>渲染高度防呆上限。</summary>
    private const int MaxHeight = 2160;

    /// <summary>swscale 双线性插值（FFmpeg 头文件的 SWS_BILINEAR 宏；AutoGen 未生成该常量）。</summary>
    private const int SwsBilinear = 2;

    /// <summary>距循环终点该源秒数内即启动预卷：预卷要在终点前完成打开、对齐与前若干帧解码。</summary>
    private const double PrerollTriggerSeconds = 2.0;

    /// <summary>预卷预解码帧数：收编后按节拍先消费这些帧，为就地续解吸收调度抖动。</summary>
    private const int PrerollFrames = 10;

    /// <summary>循环点分析窗口：头/尾各分析的源秒数（v2 加宽到 3s：片头 logo 渐入等场景下
    /// 2s 头窗可能全是废帧；分析成本在顺序解码本身，窗口只加缩略内存）。</summary>
    private const double AnalysisWindowSeconds = 3.0;

    /// <summary>时长低于该值（秒）不做循环点分析（窗口过窄没有搜索价值）。</summary>
    private const double MinAnalysisDuration = 2.0;

    /// <summary>分析窗口单侧最多采集的帧数（防御异常高帧率或坏时长导致内存膨胀）。</summary>
    private const int MaxAnalyzedFrames = 240;

    private readonly Lock _gate = new();

    /// <summary>当前帧位图（解码线程写、UI 渲染线程读，位图内部缓冲自身同步）。</summary>
    private WriteableBitmap? _frame;

    /// <summary>循环回卷的淡化层：上一循环的末帧位图，随新循环逐帧淡出后释放。</summary>
    private WriteableBitmap? _fadeFrame;

    /// <summary>淡化层当前不透明度。</summary>
    private double _fadeOpacity;

    /// <summary>每帧递减的淡化步长（按帧率与淡化时长折算）。淡化时长随接缝差自适应
    /// （<see cref="SeamAnalyzer.PickCrossfadeDuration"/>），不再用固定常量。</summary>
    private double _fadeStep;

    /// <summary>当前播放的取消源（Stop 时取消解码/渲染循环）。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>播放代际：Play/Stop 各递增一次；循环凭代际比对丢弃旧代的输出（避免 Stop/Play 竞争）。</summary>
    private int _generation;

    /// <summary>续播门（初始放行）：Pause 复位后解码循环在帧处理点泊车，Resume/新会话/Stop 放行。
    /// 与取消令牌一起 WaitAny——暂停中 Stop（切游戏/退出）靠取消令牌唤醒泊车线程正常退出。</summary>
    private readonly ManualResetEventSlim _resumeGate = new(initialState: true);

    /// <summary>活动会话标志（0/1）：PlayAsync 启动置位、后台任务收尾归零；Pause/Resume 据此判定 no-op。</summary>
    private int _sessionActive;

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
        _resumeGate.Set();
        Interlocked.Exchange(ref _sessionActive, 1);

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
                // 自然结束（循环自愈退出/解码失败）路径不经过 Cancel：先把仍指向本 cts 的
                // 共享引用摘掉再释放，否则 _cts 悬挂已释放实例，下一次 Stop() 对其 Cancel
                // 抛 ObjectDisposedException（切页/切游戏的 SetDetailActive→StopVideo 即崩）。
                // 会话标志只在仍是本代时清零：被新一代 Play 抢先后，本任务的收尾不得把
                // 新会话标成失活（VM 续播快路径会因此误走重启）
                Interlocked.CompareExchange(ref _cts, null, cts);
                if (Interlocked.CompareExchange(ref _generation, 0, 0) == generation)
                {
                    Interlocked.Exchange(ref _sessionActive, 0);
                }

                cts.Dispose();
            }
        }, CancellationToken.None);

        return await started.Task;
    }

    /// <inheritdoc/>
    public void Stop() => StopCore();

    /// <inheritdoc/>
    public bool IsSessionActive => Interlocked.CompareExchange(ref _sessionActive, 0, 0) != 0;

    /// <inheritdoc/>
    public void Pause()
    {
        // 无会话时不得留下复位门：否则下一次 PlayAsync 起播即泊车（新会话启动时也会 Set 兜底）
        if (IsSessionActive)
        {
            _resumeGate.Reset();
        }
    }

    /// <inheritdoc/>
    public void Resume() => _resumeGate.Set();

    /// <summary>测试观察点：后台任务收尾是否已摘除共享 cts 引用（回归测试等待落定用）。</summary>
    internal bool CtsClearedForTest => Interlocked.CompareExchange(ref _cts, null, null) is null;

    /// <summary>测试注入点：直接布置已释放的 cts，确定性复现 Stop 撞上悬挂引用的场景。</summary>
    internal CancellationTokenSource? CtsForTest { set => _cts = value; }

    /// <summary>停止播放：取消解码循环、推进代际并清空帧缓冲（渲染层立即回到海报/渐变兜底；
    /// 共享播放器切游戏时，迟到的陈旧帧通知以空帧缓冲为证不再点亮新页面）。</summary>
    private void StopCore()
    {
        Interlocked.Increment(ref _generation);
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // PlayAsync 收尾恰好先释放了 cts（摘除与 Exchange 之间的残余竞态）：
                // 循环已自然结束，无需取消
            }
        }

        // 暂停中泊车的解码循环靠取消令牌唤醒退出；这里放行门仅作冗余兜底（无副作用）。
        // 会话标志同步清零：接口语义"未停止"，且 Dispose() 直接委托本方法——不清的话
        // 陈旧标志会让续播判定误判（VM 侧另有 _videoSubscribed 双重守卫，此处收口契约）
        _resumeGate.Set();
        Interlocked.Exchange(ref _sessionActive, 0);
        ClearFrame();
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
        nint pixelBufferSize = 0;
        PrerollHandoff<PrerollPayload>? preroll = null;
        var pendingFrames = new List<nint>();
        var pendingFramePts = new List<double>();
        var token = cts.Token;
        try
        {
            active = OpenDecodeSource(path, softwareOnly: false);
            packet = av_packet_alloc();
            frame = av_frame_alloc();
            softwareFrame = av_frame_alloc();
            var fps = active.Fps;
            var timeBase = active.TimeBase;
            var duration = active.DurationSeconds;

            // 智能循环点：头/尾窗口找最相似帧对（v2：排名候选供多循环点轮换），把接缝落在
            // 几乎相同的画面之间；时间基/帧率/时长不齐就整段循环，靠接缝自适应兜底。
            // 起播不 seek：从流头顺序读取、由 aligningToStart 在渲染前丢弃循环起点之前的帧
            // （带时间戳的 seek 在部分环境的新开 demuxer 上不可靠，顺序读包最稳妥）
            var loopPlan = timeBase > 0 && fps > 0 && duration >= MinAnalysisDuration
                ? AnalyzeLoopPoints(path, duration, token)
                : [];
            if (loopPlan.Count == 0)
            {
                loopPlan = [(0.0, 0.0)]; // 分析完全不可行：整段循环，接缝处走最长淡化
            }

            if (loopPlan[0].Start > 0)
            {
                logger?.LogInformation(
                    "Video loop plan: {Pairs} (duration {Duration:F2}s)",
                    string.Join(", ", loopPlan.Select(p => $"{p.Start:F2}s→{p.End:F2}s")), duration);
            }
            else
            {
                logger?.LogDebug("Video loop point analysis found no match, looping full clip");
            }

            // PTS 节拍（解码多快播多快会呈数倍速快进）；pts 与帧率都拿不到时保持不节拍
            var clock = timeBase > 0 || fps > 0 ? new PlaybackClock() : null;
            var halfFrame = fps > 0 ? 0.5 / fps : 0.001;
            long frameIndex = 0;
            // 多循环点轮换：本圈从 loopStartPts 起播、在 loopEndPts 截断；每过一圈 HandleLoopEnd
            // 轮换到下一候选——重复周期翻倍且每圈路径不同，"看得出在循环"的感觉显著下降。
            // 切口始终是分析配对（尾帧→头帧），接缝匹配性不受轮换影响
            var passIndex = 0;
            var (loopStartPts, loopEndPts) = loopPlan[0];
            var aligningToStart = loopStartPts > 0;
            var loopEndReached = false;
            var prerollKicked = false;
            var lastRenderedWidth = 0;
            var lastRenderedHeight = 0;
            double lastRenderedPts = -1;
            var notify = new NotifyThrottle();
            var failures = new DecodeFailureLog(logger);
            var guard = new DecodeGuard();

            // 呈现一帧软帧：起点对帧丢弃 → 循环终点截断 → PTS 节拍等待 → 上屏 → 预卷触发；
            // 等待期间取消直接返回（外层 while 检查 token 退出），到达循环终点置 loopEndReached
            void PresentFrame(AVFrame* softFrame, double ptsSeconds)
            {
                // 代际门：被新一代 Play/Stop 取代后禁止再写入共享帧缓冲——否则迟到的旧循环帧
                // 会在 ClearFrame 之后经 EnsureFrame 重建位图，把旧画面"复活"到新游戏页面上
                if (Interlocked.CompareExchange(ref _generation, 0, 0) != generation)
                {
                    return;
                }

                if (aligningToStart)
                {
                    if (!double.IsNaN(ptsSeconds) && ptsSeconds < loopStartPts - halfFrame)
                    {
                        return; // 起播从流头顺序读取，渲染前丢弃循环起点之前的帧
                    }

                    aligningToStart = false;
                }

                // 循环终点截断（v2 边界修复）：渲染分析选中的尾帧本身、丢弃其后一帧——
                // 旧判据 `>=` 把选中尾帧丢掉，实际切口落在"尾帧前一帧→头帧"，与分析最优对
                // 错位一帧（快运动下可感知，接缝差也因此在运行时与分值脱节）
                if (loopEndPts > 0 && !double.IsNaN(ptsSeconds) && ptsSeconds > loopEndPts + halfFrame)
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

                RenderFrame(softFrame, ref scaler, ref pixelBuffer, ref pixelBufferSize, notify, failures, generation);
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

            // 循环终点处理：预卷就绪则零间隙收编（按接缝差选硬化切/自适应时长交叉淡化），
            // 否则回退重开全新解码源 + 最长交叉淡化。两路径收尾都轮换到下一循环点。
            // 返回 false = 无法继续（应结束循环）
            bool HandleLoopEnd()
            {
                // 取消（停止/退出）后不再重开/收编解码源：退出期 GPU 栈可能已坏，
                // 设备创建只会向 stderr 刷错（外层 token 检查与这里之间存在取消落入的窗口）
                if (token.IsCancellationRequested)
                {
                    return false;
                }

                loopEndReached = false;
                if (preroll is not null && preroll.TryTake(out var payload))
                {
                    var seamDiff = ComputeSeamDiff(pixelBuffer, lastRenderedWidth, lastRenderedHeight, payload.FirstThumb);
                    var hardCut = SeamAnalyzer.ShouldHardCut(seamDiff);
                    var fadeSeconds = hardCut ? 0 : SeamAnalyzer.PickCrossfadeDuration(seamDiff);
                    logger?.LogInformation(
                        "Video loop seam: diff {Diff:F1} → {Mode}{Fade}",
                        seamDiff,
                        hardCut ? "hard cut" : "crossfade",
                        hardCut ? "" : $" {fadeSeconds:F2}s");
                    if (!hardCut)
                    {
                        // 先把旧末帧转成淡化层，再让收编源的新帧上屏（时长随接缝差自适应：
                        // 轻微错配走短微淡化，固定 0.6s 长溶解本身就是醒目的循环信号）
                        PrepareLoopCrossfade(fps, fadeSeconds);
                    }

                    active.Dispose();
                    active = payload.Source;
                    pendingFrames = payload.Frames;
                    pendingFramePts = payload.PendingPts;
                    frameIndex = 0;
                    aligningToStart = false;
                    preroll = null;
                    prerollKicked = false;
                    guard.Reset(); // 健康交接自带自愈：失败计数清零
                    clock?.Reset();
                    RotateLoopPlan();
                    return true;
                }

                // 预卷未就绪：回退 = 重新打开全新解码源（不 seek——本构建的 mov seek 不可靠，
                // 顺序读取最稳妥）；打开停顿由最长交叉淡化掩盖。连续多路解码源都未产出有效回
                // （GPU 解码栈坏死，如退出期平台拆除/驱动重置）则停止播放，帧清空后由海报兜底
                if (guard.OnPassEnd((int)frameIndex, fps))
                {
                    logger?.LogInformation(
                        "Video backdrop stopped: {Passes} consecutive decode sources produced no viable pass (lastPts {Pts:F2}s)",
                        DecodeGuard.MaxDeadPasses, lastRenderedPts);
                    return false;
                }

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
                // 对齐到当前（轮换前的）头帧：刚离开的正是当前配对的尾帧，切口须落在配对上
                aligningToStart = loopStartPts > 0;
                clock?.Reset();
                PrepareLoopCrossfade(fps, SeamAnalyzer.MaxCrossfadeSeconds);
                RotateLoopPlan();
                return true;
            }

            // 轮换循环点：本圈起点已定（收编帧/对齐丢弃都指向当前头帧），终点换下一候选的尾帧；
            // 下一次预卷随之对齐到新头帧——切口永远是"某候选的尾帧 → 该候选的头帧"配对
            void RotateLoopPlan()
            {
                if (loopPlan.Count <= 1)
                {
                    return;
                }

                passIndex++;
                (loopStartPts, loopEndPts) = loopPlan[passIndex % loopPlan.Count];
            }

            while (!token.IsCancellationRequested)
            {
                // 暂停泊车：Pause 后解码循环在此挂起，不再消费帧/节拍/上屏——帧缓冲与解码源
                // 原样保留（页外不占解码资源）。唤醒只认续播门或取消令牌（暂停中 Stop 切
                // 游戏/退出经取消令牌正常退出）；醒来重定节拍基线：暂停时长不计入时间轴，
                // 下一帧立即呈现后恢复 PTS 节拍
                if (!_resumeGate.IsSet)
                {
                    if (WaitHandle.WaitAny([_resumeGate.WaitHandle, token.WaitHandle]) == 1
                        || token.IsCancellationRequested)
                    {
                        break;
                    }

                    clock?.Reset();
                }

                if (pendingFrames.Count > 0)
                {
                    // 收编的预解码帧优先消费：这是零间隙续播的关键路径
                    var pending = (AVFrame*)pendingFrames[0];
                    pendingFrames.RemoveAt(0);
                    var pendingPts = pendingFramePts[0];
                    pendingFramePts.RemoveAt(0);
                    PresentFrame(pending, pendingPts);
                    av_frame_free(&pending);
                    if (loopEndReached && !HandleLoopEnd())
                    {
                        break;
                    }

                    continue;
                }

                if (!TryDecodeNextSoftFrame(active, packet, frame, softwareFrame, token, out var pts, failures, guard))
                {
                    // 取消（停止/退出）立即结束：退出期 GPU 解码栈可能已坏，重开解码源只会制造新的失败输出
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    // 流结束（或不可恢复的读错误/解码器坏死）：走循环终点处理
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
                av_frame_unref(softwareFrame);
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
                av_packet_free(&packet);
            }

            if (frame is not null)
            {
                av_frame_free(&frame);
            }

            if (softwareFrame is not null)
            {
                av_frame_free(&softwareFrame);
            }

            if (scaler is not null)
            {
                sws_freeContext(scaler);
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
    /// 智能循环点分析：解码头/尾各约一个窗口的帧并采集 RGB 缩略，搜索最相似帧对排名
    /// （供多循环点轮换）。空列表 = 完全不可分析（调用方回退整段循环）；无阈值内命中时
    /// 返回单条降级最优（配最长淡化）。绝不抛出——起播不能因分析失败而失败。
    /// </summary>
    private unsafe List<(double Start, double End)> AnalyzeLoopPoints(
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
            packet = av_packet_alloc();
            frame = av_frame_alloc();
            softwareFrame = av_frame_alloc();

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
                if (!TryDecodeNextSoftFrame(source, packet, frame, softwareFrame, token, out var pts))
                {
                    break; // 流结束
                }

                if (double.IsNaN(pts))
                {
                    av_frame_unref(softwareFrame);
                    continue;
                }

                var thumb = ExtractRgbThumb(softwareFrame, ref thumbScaler);
                av_frame_unref(softwareFrame);
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
                return [];
            }

            return SeamAnalyzer.FindLoopPoints(headThumbs, headPts, tailThumbs, tailPts)
                .Select(m => (headPts[m.HeadIndex], tailPts[m.TailIndex]))
                .ToList();
        }
        catch (Exception ex)
        {
            logger?.LogInformation(ex, "Video loop point analysis failed");
            return [];
        }
        finally
        {
            if (thumbScaler is not null)
            {
                sws_freeContext(thumbScaler);
            }

            if (packet is not null)
            {
                av_packet_free(&packet);
            }

            if (frame is not null)
            {
                av_frame_free(&frame);
            }

            if (softwareFrame is not null)
            {
                av_frame_free(&softwareFrame);
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
            if (!TryDecodeNextSoftFrame(source, packet, frame, softwareFrame, token, out var pts))
            {
                return;
            }

            if (double.IsNaN(pts))
            {
                // 无 pts 的帧无法参与循环点定位：跳过
                av_frame_unref(softwareFrame);
                continue;
            }

            if (pts > untilSeconds)
            {
                av_frame_unref(softwareFrame);
                return;
            }

            var thumb = ExtractRgbThumb(softwareFrame, ref thumbScaler);
            if (thumb is not null)
            {
                thumbs.Add(thumb);
                ptsSeconds.Add(pts);
            }

            av_frame_unref(softwareFrame);
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
            packet = av_packet_alloc();
            frame = av_frame_alloc();
            softwareFrame = av_frame_alloc();
            frames = [];
            var pendingPts = new List<double>();
            var halfFrame = source.Fps > 0 ? 0.5 / source.Fps : 0.001;
            var prerollGuard = new DecodeGuard();
            byte[]? firstThumb = null;
            while (frames.Count < PrerollFrames && !token.IsCancellationRequested)
            {
                if (!TryDecodeNextSoftFrame(source, packet, frame, softwareFrame, token, out var pts, guard: prerollGuard))
                {
                    break;
                }

                // 精确对帧：丢弃循环起点之前的帧
                if (loopStartPts > 0 && !double.IsNaN(pts) && pts < loopStartPts - halfFrame)
                {
                    av_frame_unref(softwareFrame);
                    continue;
                }

                var copy = av_frame_alloc();
                if (av_frame_ref(copy, softwareFrame) == 0)
                {
                    frames.Add((nint)copy);
                    pendingPts.Add(pts);
                    firstThumb ??= ExtractRgbThumb(softwareFrame, ref thumbScaler);
                }

                av_frame_unref(softwareFrame);
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
                av_packet_free(&packet);
            }

            if (frame is not null)
            {
                av_frame_free(&frame);
            }

            if (softwareFrame is not null)
            {
                av_frame_free(&softwareFrame);
            }

            if (thumbScaler is not null)
            {
                sws_freeContext(thumbScaler);
            }
        }
    }

    /// <summary>打开一路解码源（输入 + 流元数据 + 解码器）；失败时释放已创建资源后原样抛出。</summary>
    private unsafe DecodeSource OpenDecodeSource(string path, bool softwareOnly)
    {
        var formatContext = OpenInput(path);
        try
        {
            var streamIndex = av_find_best_stream(
                formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (streamIndex < 0)
            {
                throw new InvalidOperationException($"Video has no video stream: {path}");
            }

            var stream = formatContext->streams[streamIndex];
            var timeBase = av_q2d(stream->time_base);
            var fps = av_q2d(stream->avg_frame_rate);
            if (fps <= 0)
            {
                fps = av_q2d(stream->r_frame_rate);
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
            avformat_close_input(&formatContext);
            throw;
        }
    }

    /// <summary>打开输入文件（路径按 UTF-8 编组）。</summary>
    private unsafe AVFormatContext* OpenInput(string path)
    {
        AVFormatContext* context = null;
        if (avformat_open_input(&context, path, null, null) != 0)
        {
            throw new InvalidOperationException($"Cannot open video: {path}");
        }

        return context;
    }

    /// <summary>打开解码器：按平台顺序试硬解（Windows D3D11VA / Linux VAAPI→NVDEC），全部创建失败回软解；硬解设备引用经 <paramref name="device"/> 返回。
    /// <paramref name="softwareOnly"/> = true 时刻意纯软解（循环点分析），不打硬解相关日志。</summary>
    private unsafe AVCodecContext* OpenDecoder(AVCodecParameters* parameters, ref AVBufferRef* device, bool softwareOnly = false)
    {
        var decoder = avcodec_find_decoder(parameters->codec_id);
        if (decoder is null)
        {
            throw new InvalidOperationException("No decoder for codec");
        }

        var context = avcodec_alloc_context3(decoder);
        avcodec_parameters_to_context(context, parameters);

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
            var createResult = av_hwdevice_ctx_create(&created, hwType, null, null, 0);
            if (createResult == 0)
            {
                context->hw_device_ctx = av_buffer_ref(created);
                device = created;
                logger?.LogDebug("Video backdrop hardware device created: {Type}", hwType);
                break;
            }

            created = null;
            logger?.LogDebug(
                "Video backdrop hardware device {Type} create failed: {Error} ({Reason})",
                hwType, createResult, DescribeFfmpegError(createResult));
        }

        // 只有真正尝试过硬解（softwareOnly 的分析源刻意软解）才打回退日志，避免把设计当故障
        if (context->hw_device_ctx is null && !softwareOnly)
        {
            logger?.LogDebug("Video backdrop hardware decode unavailable, falling back to software");
        }

        if (avcodec_open2(context, decoder, null) < 0)
        {
            avcodec_free_context(&context);
            throw new InvalidOperationException("Cannot open video decoder");
        }

        // 设备创建成功 ≠ 硬解生效：解码器仍可能与驱动协商回软解格式（如 NVIDIA nvidia-vaapi-driver
        // EGL 模式不支持解码），协商出的像素格式才是硬解真正工作的判据
        if (!softwareOnly)
        {
            var pixelFormat = context->pix_fmt;
            logger?.LogDebug(
                "Video backdrop decoder negotiated {Width}x{Height} {Format} ({Mode})",
                context->width, context->height,
                PixelFormatName(pixelFormat), IsHardwarePixelFormat(pixelFormat) ? "hardware" : "software");
        }

        return context;
    }

    /// <summary>把 FFmpeg 错误码转成可读文本（av_strerror）；无对应描述回退 "unknown error"。</summary>
    private static unsafe string DescribeFfmpegError(int errorCode)
    {
        var buffer = stackalloc byte[256];
        return av_strerror(errorCode, buffer, 256) == 0
            ? Marshal.PtrToStringAnsi((nint)buffer) ?? "unknown error"
            : "unknown error";
    }

    /// <summary>像素格式名（av_get_pix_fmt_name）；未知格式回退枚举名。</summary>
    private static string PixelFormatName(AVPixelFormat format)
    {
        var name = av_get_pix_fmt_name(format);
        return string.IsNullOrEmpty(name) ? format.ToString() : name;
    }

    /// <summary>协商格式是否为硬件格式（与 hwTypes 一一对应：VAAPI / CUDA / D3D11）。</summary>
    private static bool IsHardwarePixelFormat(AVPixelFormat format) =>
        format is AVPixelFormat.AV_PIX_FMT_VAAPI
            or AVPixelFormat.AV_PIX_FMT_CUDA
            or AVPixelFormat.AV_PIX_FMT_D3D11;

    /// <summary>读取并解码下一帧软帧：硬解输出回读系统内存，软解转移引用。<paramref name="ptsSeconds"/>
    /// 取自原始解码帧（硬解回读不拷贝时间戳属性，必须在回读前捕获）；返回 false = 流结束、取消或无法继续
    /// （取消/连续无输出包超限由调用方按终止处理）。</summary>
    private static unsafe bool TryDecodeNextSoftFrame(
        DecodeSource source,
        AVPacket* packet,
        AVFrame* frame,
        AVFrame* softwareFrame,
        CancellationToken token,
        out double ptsSeconds,
        DecodeFailureLog? failures = null,
        DecodeGuard? guard = null)
    {
        ptsSeconds = double.NaN;
        while (true)
        {
            // 停止/退出：立即终止，不再喂数据（退出期 GPU 栈可能已坏，喂包只会向 stderr 刷错）
            if (token.IsCancellationRequested)
            {
                return false;
            }

            var readResult = av_read_frame(source.FormatContext, packet);
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
                // 音频/字幕/封面等其他流：直接跳过（背景视频不解码音轨，不计入无输出计数）
                av_packet_unref(packet);
                continue;
            }

            var sent = avcodec_send_packet(source.CodecContext, packet) >= 0;
            av_packet_unref(packet);
            if (!sent)
            {
                failures?.Log("packet rejected by decoder (stream {Stream})", source.StreamIndex);
                if (guard?.OnPacketWithoutFrame() == true)
                {
                    return false;
                }

                continue;
            }

            if (avcodec_receive_frame(source.CodecContext, frame) < 0)
            {
                // 解码器内部缓冲未出帧（EAGAIN，含 B 帧重排）：继续喂数据；
                // 连续无输出超过合法重排深度数倍即判解码器坏死
                if (guard?.OnPacketWithoutFrame() == true)
                {
                    return false;
                }

                continue;
            }

            guard?.OnFrameDecoded();
            // 原始帧的时间戳由解码器写入；回读/转移后再读会丢失
            var capturedPts = BestEffortPts(frame, source.TimeBase);
            bool ok;
            if (frame->hw_frames_ctx is not null)
            {
                // 硬解输出的是 GPU 帧：回读到系统内存再进统一管线（PCIe 回读开销极小）
                ok = av_hwframe_transfer_data(softwareFrame, frame, 0) >= 0;
                if (!ok)
                {
                    failures?.Log("hw frame transfer failed");
                }
            }
            else
            {
                // 软解：把解码帧引用转移到 softwareFrame，统一后续管线
                ok = av_frame_ref(softwareFrame, frame) == 0;
            }

            av_frame_unref(frame);
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

    /// <summary>把软帧缩略成固定尺寸 RGB 图（分析用小尺寸 sws，缩略上下文按源格式缓存复用；
    /// v2 保留彩色——灰度缩略会漏掉同亮度不同色相的跳变）；失败返回 null。</summary>
    private static unsafe byte[]? ExtractRgbThumb(AVFrame* softFrame, ref SwsContext* thumbScaler)
    {
        if (softFrame->width <= 0 || softFrame->height <= 0)
        {
            return null;
        }

        thumbScaler = sws_getCachedContext(
            thumbScaler, softFrame->width, softFrame->height, (AVPixelFormat)softFrame->format,
            SeamAnalyzer.ThumbWidth, SeamAnalyzer.ThumbHeight, AVPixelFormat.AV_PIX_FMT_RGB24,
            SwsBilinear, null, null, null);
        if (thumbScaler is null)
        {
            return null;
        }

        var thumb = new byte[SeamAnalyzer.ThumbWidth * SeamAnalyzer.ThumbHeight * SeamAnalyzer.ThumbChannels];
        fixed (byte* thumbPtr = thumb)
        {
            var destination = new byte_ptrArray4 { [0] = thumbPtr };
            var destinationLines = new int_array4 { [0] = SeamAnalyzer.ThumbWidth * SeamAnalyzer.ThumbChannels };
            sws_scale(thumbScaler, softFrame->data, softFrame->linesize, 0, softFrame->height,
                destination, destinationLines);
        }

        return thumb;
    }

    /// <summary>接缝差：旧循环末帧（仍在像素暂存缓冲里）与预卷首帧缩略的综合分
    /// （全局平均 + 分块惩罚）；无末帧视为最大（必然走淡化）。</summary>
    private static unsafe double ComputeSeamDiff(byte* pixelBuffer, int width, int height, byte[] prerollFirstThumb)
    {
        if (pixelBuffer is null || width <= 0 || height <= 0)
        {
            return double.MaxValue;
        }

        var lastThumb = SeamAnalyzer.DownsampleBgraToRgb(
            pixelBuffer, width, height, width * 4, SeamAnalyzer.ThumbWidth, SeamAnalyzer.ThumbHeight);
        return SeamAnalyzer.Difference(lastThumb, prerollFirstThumb);
    }

    /// <summary>释放帧引用队列（每帧 av_frame_free，队列随之清空）。</summary>
    private static unsafe void FreeFrames(List<nint> frames)
    {
        foreach (var framePtr in frames)
        {
            var pointer = (AVFrame*)framePtr;
            if (pointer is not null)
            {
                av_frame_free(&pointer);
            }
        }

        frames.Clear();
    }

    /// <summary>预卷负载的清理回调：释放预解码帧与解码源（交接状态机在恰当时机调用恰好一次）。</summary>
    private static void FreePrerollPayload(PrerollPayload payload)
    {
        FreeFrames(payload.Frames);
        payload.Source.Dispose();
    }

    /// <summary>
    /// 循环回卷的交叉淡化准备：旧循环末帧保留为淡化层（整体淡出掩盖接缝），
    /// 新循环写全新位图不受旧层覆盖；淡化步长按帧率与 <paramref name="fadeSeconds"/> 折算
    /// （时长随接缝差自适应，见 <see cref="SeamAnalyzer.PickCrossfadeDuration"/>）。
    /// </summary>
    private void PrepareLoopCrossfade(double fps, double fadeSeconds)
    {
        lock (_gate)
        {
            if (_frame is null)
            {
                return;
            }

            _fadeFrame = _frame;
            _fadeStep = fps > 0 ? 1.0 / (fps * fadeSeconds) : 1.0 / (4 * fadeSeconds);
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

    /// <summary>单帧处理：确保缩放器与缓冲匹配源格式 → swscale 到 BGRA → blit 进位图 → 节流通知。
    /// generation 为本代循环代号：过代际门后的 PTS 等待与 sws/拷贝期间可能发生 Stop/新一代起播，
    /// 拷入位图后复查代际，失配则整帧丢弃且不投递通知（清帧由 Stop 在锁内完成）。</summary>
    private unsafe void RenderFrame(
        AVFrame* source,
        ref SwsContext* scaler,
        ref byte* pixelBuffer,
        ref nint pixelBufferSize,
        NotifyThrottle notify,
        DecodeFailureLog failures,
        int generation)
    {
        var (width, height) = ClampEven(source->width, source->height);
        if (width <= 0 || height <= 0)
        {
            failures.Log("invalid frame size {Width}x{Height}", source->width, source->height);
            return;
        }

        // 代际已换代（PresentFrame 过门到这里的窗口内发生了 Stop）：不再做无谓的缩放与位图写入
        if (Interlocked.CompareExchange(ref _generation, 0, 0) != generation)
        {
            return;
        }

        // 源格式/尺寸变化（硬解回读后的像素格式与软解不同）时重建缩放器
        scaler = sws_getCachedContext(
            scaler, source->width, source->height, (AVPixelFormat)source->format,
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA, SwsBilinear, null, null, null);
        if (scaler is null)
        {
            failures.Log("cannot create swscale context");
            return;
        }

        var stride = width * 4;
        var required = (nint)stride * height;
        if (pixelBuffer is null || pixelBufferSize < required)
        {
            // 先分配新缓冲、成功后再释放旧的：分配失败（OOM）时 ref 仍指向有效旧缓冲，
            // RunLoop 收尾只会释放一次，不会双重释放
            var replacement = (byte*)NativeMemory.AlignedAlloc((nuint)required, 64);
            if (pixelBuffer is not null)
            {
                NativeMemory.AlignedFree(pixelBuffer);
            }

            pixelBuffer = replacement;
            pixelBufferSize = required;
        }

        var destination = new byte_ptrArray4 { [0] = pixelBuffer };
        var destinationLines = new int_array4 { [0] = stride };
        sws_scale(scaler, source->data, source->linesize, 0, source->height,
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

        // 代际收尾复查（锁外，仅丢弃不缓存）：过门后的 PTS 等待/sws/拷贝窗口内若发生 Stop/换代，
        // 此帧不投递通知即被丢弃——旧画面无从"复活"。清帧由 Stop 的 ClearFrame 在 _gate 锁内完成，
        // 此处不再代劳：既避免锁外清帧与新一代首帧的竞争，也让通知与帧状态保持一致
        if (Interlocked.CompareExchange(ref _generation, 0, 0) != generation)
        {
            return;
        }

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
                    PixelFormats.Bgra8888, AlphaFormat.Opaque);
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

    /// <summary>一路打开的视频解码源（输入 + 解码器 + 硬解设备）与流元数据；循环接缝处整体收编替换。</summary>
    [ExcludeFromCodeCoverage]
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
                avcodec_free_context(&codecContext);
            }

            var hwDevice = HwDevice;
            if (hwDevice is not null)
            {
                HwDevice = null;
                av_buffer_unref(&hwDevice);
            }

            var formatContext = FormatContext;
            if (formatContext is not null)
            {
                FormatContext = null;
                avformat_close_input(&formatContext);
            }
        }
    }

    /// <summary>预卷负载：已对齐循环起点的解码源 + 预解码的软件帧队列（含捕获的 pts）+ 首帧灰度缩略。</summary>
    [ExcludeFromCodeCoverage]
    private sealed class PrerollPayload(DecodeSource source, List<nint> frames, List<double> pendingPts, byte[] firstThumb)
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
internal sealed class PlaybackClock(double rate = 1.0)
{
    /// <summary>落后超过该值（毫秒）即重定基线（回卷/卡顿后直接恢复，不爆发追赶）。</summary>
    private const double RebaseThresholdMs = 250;

    private readonly Stopwatch _clock = new();

    /// <summary>播放速率（1.0 = 片源原生速度；预留给未来调速，当前恒为原生）。</summary>
    private readonly double _rate = rate > 0 ? rate : 1.0;

    private double? _basePts;

    private double _baseElapsedMs;

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

/// <summary>
/// 解码韧性熔断器（纯状态机，internal 供单测）：区分「流正常走完」与「解码器坏死」。
/// GPU 解码栈失效（驱动重置、应用退出期的平台拆除弄坏 VAAPI/NVDEC 等）时每个视频包都无输出帧，
/// FFmpeg 会以每帧两条的速度向 stderr 刷 "hardware accelerator failed"。两级熔断把最坏输出
/// 限制在有限几条并停止播放，帧位图清空后由静态海报兜底。阈值依据：正常 h264 B 帧重排深度
/// 上限 16 帧，无输出包阈值必须明显高于它。
/// </summary>
internal sealed class DecodeGuard
{
    /// <summary>连续无输出视频包上限（2×h264 最大重排深度 16）：超过即判解码器坏死，放弃当前解码源。</summary>
    internal const int MaxPacketsWithoutFrame = 32;

    /// <summary>连续「未产出有效回」的解码源个数上限：超过后停止播放，等下次起播再试。</summary>
    internal const int MaxDeadPasses = 3;

    /// <summary>有效回的最少渲染帧数：不足 0.25s（且至少 8 帧）视为未真正起播。</summary>
    internal static int MinViablePassFrames(double fps) => Math.Max(8, (int)(fps * 0.25));

    private int _packetsWithoutFrame;

    private int _deadPasses;

    /// <summary>记录一个未产出帧的视频包；返回 true = 连续无输出超限，应放弃当前解码源。</summary>
    public bool OnPacketWithoutFrame() => ++_packetsWithoutFrame >= MaxPacketsWithoutFrame;

    /// <summary>记录一次成功输出的解码帧（无输出计数清零）。</summary>
    public void OnFrameDecoded() => _packetsWithoutFrame = 0;

    /// <summary>
    /// 一路解码源走到终点（EOF/坏死/取消以外的终止）：本回渲染帧数达到有效回标准即清零失败计数，
    /// 否则累计；返回 true = 连续坏死次数超限，应停止播放。
    /// </summary>
    public bool OnPassEnd(int renderedFrames, double fps)
    {
        _packetsWithoutFrame = 0;
        _deadPasses = renderedFrames >= MinViablePassFrames(fps) ? 0 : _deadPasses + 1;
        return _deadPasses >= MaxDeadPasses;
    }

    /// <summary>预卷源健康接管播放：失败计数与无输出包计数清零（交接自带自愈，新源重新计账）。</summary>
    public void Reset()
    {
        _deadPasses = 0;
        _packetsWithoutFrame = 0;
    }
}
