namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 下载速度平滑器（2026-10-02，RF-2）：对 <c>UpdateProgress.DownloadedBytes</c> 序列按
/// ≥0.4s 间隔采样、EMA 平滑（新样本权重 0.4），字节回退（校验失败重下）重置样本。
/// 停滞帧语义（RF-10 注记）：同字节超过 0.4s 的样本 instant=0 仍混入 EMA——速度向 0 衰减、
/// 数帧内归零（UI 显示"—"）。停滞即无速度为有意语义（更真实地反映"当前没有数据到达"），
/// 与旧实现的"冻结旧速度"是文档化分歧，勿按旧语义"修复"。
/// <see cref="Reset"/> 在操作复位点与非下载阶段调用——新一轮操作不得瞬时残留上一轮的速度
///（此前样本跨操作存活，首帧可能显示旧速度）。时钟可注入（测试用 ManualTimeProvider）。
/// </summary>
internal sealed class DownloadSpeedSmoother(TimeProvider? clock = null)
{
    private const double MinimumSampleSeconds = 0.4;
    private const double NewSampleWeight = 0.4;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _sampleBytes;
    private long _sampleTimestamp;

    /// <summary>EMA 平滑后的当前速度（字节/秒）；0 = 尚无有效样本或已重置。</summary>
    public double BytesPerSecond { get; private set; }

    /// <summary>喂入一帧累计字节数（Downloading 阶段逐帧调用）。</summary>
    public void OnProgressBytes(long downloadedBytes)
    {
        var now = _clock.GetTimestamp();
        var sampleSeconds = (now - _sampleTimestamp) / (double)_clock.TimestampFrequency;
        if (downloadedBytes < _sampleBytes)
        {
            // 字节回退 = 校验失败丢弃重下：样本重置，速度清零待新样本
            BytesPerSecond = 0;
            _sampleBytes = downloadedBytes;
            _sampleTimestamp = now;
            return;
        }

        if (sampleSeconds < MinimumSampleSeconds)
        {
            return;
        }

        var instant = downloadedBytes > _sampleBytes
            ? (downloadedBytes - _sampleBytes) / sampleSeconds
            : 0;
        BytesPerSecond = BytesPerSecond <= 0 ? instant : BytesPerSecond * (1 - NewSampleWeight) + instant * NewSampleWeight;
        _sampleBytes = downloadedBytes;
        _sampleTimestamp = now;
    }

    /// <summary>清零速度与采样点（操作复位点 / 阶段切换离开下载）。采样时间戳锚定当前时钟
    /// 而非归零——归零会让新轮首帧按「自时钟原点」的巨大间隔立即采样，产出一帧无意义的微速度。</summary>
    public void Reset()
    {
        BytesPerSecond = 0;
        _sampleBytes = 0;
        _sampleTimestamp = _clock.GetTimestamp();
    }
}
