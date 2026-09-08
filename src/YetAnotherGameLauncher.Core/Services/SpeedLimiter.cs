namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// 全局下载限速节流器（泄漏桶排队）：所有下载调用共享同一预算，
/// Acquire 按请求字节数排队并返回需要等待的时长。BytesPerSecond = 0 表示不限速。
/// TimeProvider 可注入以便测试。
/// </summary>
public sealed class SpeedLimiter
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private long _bytesPerSecond;
    private long _nextFreeTimestamp;

    public SpeedLimiter(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>当前限速（字节/秒），0 = 不限速；写入会清空已排队的发送窗口。</summary>
    public long BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _bytesPerSecond;
            }
        }
        set
        {
            lock (_gate)
            {
                _bytesPerSecond = Math.Max(0, value);
                _nextFreeTimestamp = 0; // 限速变更（含解除）时清空排队
            }
        }
    }

    /// <summary>为 bytes 字节排队带宽；返回调用方应等待的时长（零 = 立即发送）。</summary>
    public TimeSpan Acquire(int bytes)
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_bytesPerSecond <= 0)
            {
                _nextFreeTimestamp = 0;
                return TimeSpan.Zero;
            }

            var start = Math.Max(now, _nextFreeTimestamp);
            var durationTicks = (long)(_time.TimestampFrequency * bytes / (double)_bytesPerSecond);
            _nextFreeTimestamp = start + Math.Max(durationTicks, 1);

            var waitTicks = start - now;
            return waitTicks <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(waitTicks);
        }
    }
}
