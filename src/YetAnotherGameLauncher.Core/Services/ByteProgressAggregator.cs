using System.Diagnostics;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// 并发安全的字节进度聚合器：把各下载文件 <c>IProgress&lt;long&gt;</c> 的"本文件累计字节"
/// 换算为全局增量，按最小时间间隔节流投递给上层——下载器 64KB 粒度回调直发会高频轰炸
/// UI 线程（每秒数百次 post），节流后 ≤10 次/秒仍保持实时观感。
/// 增量预载 / 全量安装（并行下载）/ 包式下载三条链共用；进度口径 = 本地已就绪字节
/// （含 .temp 续传起点：下载器首回调含已有字节，delta 自然计入）。
/// </summary>
public sealed class ByteProgressAggregator
{
    private readonly Action<long>? _report;
    private readonly long _minIntervalTicks;
    private long _bytes;
    private long _lastReportAt = -1; // -1 = 尚未投递：单调钟时间戳自开机起算（恒 ≥ 0），初始 0 会让
                                     // 首帧投递取决于"机器 uptime ≥ interval"这个偶然前提
                                     //（2026-10-02 CI 实锤：新 VM uptime < 1h 吞首帧，测试假绿/假红随 uptime 掷骰）

    /// <param name="minReportInterval">两次投递的最小间隔；<see cref="TimeSpan.Zero"/> 表示全投（测试）。</param>
    /// <param name="report">节流后的回调，参数为全局累计字节。调用方线程直接执行（不 post 同步上下文）。</param>
    public ByteProgressAggregator(TimeSpan minReportInterval, Action<long>? report)
    {
        _report = report;
        _minIntervalTicks = minReportInterval == TimeSpan.Zero
            ? 0
            : (long)(minReportInterval.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>全局已就绪字节（线程安全读取）。</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>计入一个增量（可为负：校验失败丢弃重下时进度回退再上涨，UI 端有 Clamp）。</summary>
    public void Add(long delta)
    {
        if (delta == 0)
        {
            return;
        }

        Interlocked.Add(ref _bytes, delta);
        MaybeReport(force: false);
    }

    /// <summary>越过节流立即投递一次当前累计（文件完成/跳过等条目边界用）。</summary>
    public void ForceReport() => MaybeReport(force: true);

    /// <summary>为单个文件创建进度适配器：接收"该文件累计字节"，自动换算 delta 计入全局。
    /// 每个文件必须各用一个实例（保存各自的累计基准）；并发文件间互不干扰。</summary>
    public FileProgress CreateFileProgress() => new(this);

    private void MaybeReport(bool force)
    {
        if (_report is null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref _lastReportAt);
        if (!force && last >= 0 && now - last < _minIntervalTicks)
        {
            return;
        }

        Volatile.Write(ref _lastReportAt, now);
        _report(Interlocked.Read(ref _bytes));
    }

    /// <summary>单文件的 <c>IProgress&lt;long&gt;</c> 适配器：下载线程直接回调（不经同步上下文），
    /// 记录该文件已换算入全局的字节数，供完成时兜底补齐（零回调替身/期望与实收差）。</summary>
    public sealed class FileProgress(ByteProgressAggregator owner) : IProgress<long>
    {
        private long _last;
        private long _consumed;

        /// <summary>本文件已换算计入全局的字节（完成时以 <c>size - ConsumedBytes</c> 补齐，
        /// 使全局恰好 +size——无论下载器回调 0 次还是 n 次，分母分子同口径）。</summary>
        public long ConsumedBytes => Interlocked.Read(ref _consumed);

        /// <summary>接收该文件累计字节（含续传起点），换算 delta 计入全局。</summary>
        public void Report(long value)
        {
            var delta = value - _last;
            _last = value;
            if (delta == 0)
            {
                return;
            }

            Interlocked.Add(ref _consumed, delta);
            owner.Add(delta);
        }
    }
}
