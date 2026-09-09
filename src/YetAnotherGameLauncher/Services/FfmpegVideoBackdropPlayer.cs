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

/// <summary>
/// 基于 FFmpeg 的背景视频播放器：后台线程循环解码（软解为基线，D3D11VA/VAAPI 硬解自动启用），
/// 帧经 swscale 转成 BGRA 后逐行拷进 WriteableBitmap，UI 线程节流触发 <see cref="FrameUpdated"/>
/// 重绘。静音（不解码音频轨）、无缝循环（EOF 回卷重解码）、分辨率 clamp ≤1080p。
/// 解码管线与原生库获取（<see cref="FfmpegLibraryResolver"/>）解耦，可整体替换实现。
/// </summary>
public sealed class FfmpegVideoBackdropPlayer(
    FfmpegLibraryResolver libraryResolver,
    ILogger<FfmpegVideoBackdropPlayer>? logger = null) : IVideoBackdropPlayer, IDisposable
{
    /// <summary>帧通知节流间隔（≤30fps，背景不需要满帧率刷新）。</summary>
    private static readonly TimeSpan NotifyInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>背景渲染尺寸上限：解码与 blit 都按此裁剪，超出部分纯浪费。</summary>
    private const int MaxWidth = 1920;

    /// <summary>渲染高度上限。</summary>
    private const int MaxHeight = 1080;

    /// <summary>swscale 双线性插值（FFmpeg 头文件的 SWS_BILINEAR 宏；AutoGen 未生成该常量）。</summary>
    private const int SwsBilinear = 2;

    private readonly object _gate = new();

    /// <summary>当前帧位图（解码线程写、UI 渲染线程读，位图内部缓冲自身同步）。</summary>
    private WriteableBitmap? _frame;

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

    /// <summary>解码主循环：打开 → 硬解优先 → 逐帧按 PTS 节拍 swscale/BGRA → blit → EOF 回卷；退出时全部释放。</summary>
    private unsafe void RunLoop(string path, CancellationTokenSource cts, int generation)
    {
        AVFormatContext* formatContext = null;
        AVCodecContext* codecContext = null;
        AVBufferRef* hwDevice = null;
        AVPacket* packet = null;
        AVFrame* frame = null;
        AVFrame* softwareFrame = null;
        SwsContext* scaler = null;
        byte* pixelBuffer = null;
        var token = cts.Token;
        try
        {
            formatContext = OpenInput(path);
            var streamIndex = ffmpeg.av_find_best_stream(
                formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (streamIndex < 0)
            {
                logger?.LogInformation("Video has no video stream: {Path}", path);
                return;
            }

            var stream = formatContext->streams[streamIndex];
            var timeBase = ffmpeg.av_q2d(stream->time_base);
            var fps = ffmpeg.av_q2d(stream->avg_frame_rate);
            if (fps <= 0)
            {
                fps = ffmpeg.av_q2d(stream->r_frame_rate);
            }

            // PTS 节拍（解码多快播多快会呈数倍速快进）；pts 与帧率都拿不到时保持不节拍
            var clock = timeBase > 0 || fps > 0 ? new PlaybackClock() : null;
            long frameIndex = 0;

            // 无 pts 帧的呈现时刻推算：优先 pts×time_base，缺失时回退 帧序号/平均帧率；-1 = 无节拍信息
            double FramePtsSeconds(AVFrame* candidate)
            {
                if (timeBase > 0)
                {
                    var pts = candidate->best_effort_timestamp;
                    if (pts == AV_NOPTS_VALUE || pts < 0)
                    {
                        pts = candidate->pts;
                    }

                    if (pts != AV_NOPTS_VALUE && pts >= 0)
                    {
                        return pts * timeBase;
                    }
                }

                return fps > 0 ? frameIndex / fps : -1;
            }

            codecContext = OpenDecoder(stream->codecpar, ref hwDevice);
            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            softwareFrame = ffmpeg.av_frame_alloc();

            var notify = new NotifyThrottle();
            var failures = new DecodeFailureLog(logger);
            while (!token.IsCancellationRequested)
            {
                var readResult = ffmpeg.av_read_frame(formatContext, packet);
                if (readResult < 0)
                {
                    if (readResult != AVERROR_EOF)
                    {
                        failures.Log("demuxer read error {Code}", readResult);
                    }

                    if (!Rewind(formatContext, codecContext, streamIndex))
                    {
                        failures.Log("cannot rewind at end of stream, stopping loop");
                        return; // 无法回卷（文件被删/流关闭）：结束循环
                    }

                    clock?.Reset();
                    frameIndex = 0;
                    continue;
                }

                if (packet->stream_index != streamIndex)
                {
                    // 音频/字幕/封面等其他流：直接跳过（背景视频不解码音轨）
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                if (ffmpeg.avcodec_send_packet(codecContext, packet) >= 0)
                {
                    while (ffmpeg.avcodec_receive_frame(codecContext, frame) >= 0)
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        var source = frame;
                        if (frame->hw_frames_ctx is not null)
                        {
                            // 硬解输出的是 GPU 帧：回读到系统内存再进统一管线（PCIe 回读开销极小）
                            if (ffmpeg.av_hwframe_transfer_data(softwareFrame, frame, 0) < 0)
                            {
                                failures.Log("hw frame transfer failed");
                                ffmpeg.av_frame_unref(frame);
                                continue;
                            }

                            source = softwareFrame;
                        }

                        // 按 PTS 等到目标呈现时刻再上屏（等待期间取消即退出）
                        if (clock is not null)
                        {
                            var ptsSeconds = FramePtsSeconds(source);
                            if (ptsSeconds >= 0)
                            {
                                var delayMs = clock.WaitDelayMs(ptsSeconds);
                                if (delayMs is > 0
                                    && token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(delayMs.Value)))
                                {
                                    break;
                                }
                            }
                        }

                        RenderFrame(source, ref scaler, ref pixelBuffer, notify, failures);
                        frameIndex++;
                        ffmpeg.av_frame_unref(frame);
                        ffmpeg.av_frame_unref(softwareFrame);
                    }
                }
                else
                {
                    failures.Log("packet rejected by decoder (stream {Stream})", packet->stream_index);
                }

                ffmpeg.av_packet_unref(packet);
            }
        }
        finally
        {
            if (scaler is not null)
            {
                ffmpeg.sws_freeContext(scaler);
            }

            if (pixelBuffer is not null)
            {
                NativeMemory.AlignedFree(pixelBuffer);
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

            if (codecContext is not null)
            {
                ffmpeg.avcodec_free_context(&codecContext);
            }

            if (hwDevice is not null)
            {
                ffmpeg.av_buffer_unref(&hwDevice);
            }

            if (formatContext is not null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }

            // 循环结束时若已被新一代播放取代：帧位图归新一代所有，不在此清空
            if (Interlocked.CompareExchange(ref _generation, 0, 0) == generation)
            {
                ClearFrame();
            }
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

    /// <summary>打开解码器：先试硬解（Windows D3D11VA / Linux VAAPI），设备创建失败自动回软解；硬解设备引用经 <paramref name="device"/> 返回。</summary>
    private unsafe AVCodecContext* OpenDecoder(AVCodecParameters* parameters, ref AVBufferRef* device)
    {
        var decoder = ffmpeg.avcodec_find_decoder(parameters->codec_id);
        if (decoder is null)
        {
            throw new InvalidOperationException("No decoder for codec");
        }

        var context = ffmpeg.avcodec_alloc_context3(decoder);
        ffmpeg.avcodec_parameters_to_context(context, parameters);

        var hwType = OperatingSystem.IsWindows() ? AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA
            : OperatingSystem.IsLinux() ? AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI
            : AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        AVBufferRef* created = null;
        if (hwType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE
            && ffmpeg.av_hwdevice_ctx_create(&created, hwType, null, null, 0) == 0)
        {
            context->hw_device_ctx = ffmpeg.av_buffer_ref(created);
            device = created;
            logger?.LogDebug("Video backdrop hardware decode: {Type}", hwType);
        }
        else
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

    /// <summary>EOF 回卷：seek 回开头并冲刷解码器；返回 false = 无法回卷（应结束循环）。</summary>
    private static unsafe bool Rewind(AVFormatContext* formatContext, AVCodecContext* codecContext, int streamIndex)
    {
        if (ffmpeg.av_seek_frame(formatContext, streamIndex, long.MinValue, AVSEEK_FLAG_BACKWARD) < 0)
        {
            return false;
        }

        ffmpeg.avcodec_flush_buffers(codecContext);
        return true;
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
