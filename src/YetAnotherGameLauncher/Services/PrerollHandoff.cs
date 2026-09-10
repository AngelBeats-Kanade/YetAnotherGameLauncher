namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 预卷结果的交接状态机：单生产者 / 单消费者、恰好一次的资源所有权。
/// 消费者在循环接缝处 <see cref="TryTake"/> 收编预解码负载；没等到就 <see cref="Abandon"/>
/// 放弃——已完成未收编的负载由 Abandon 当场清理，仍在解码的由生产者完成时自清理，
/// 任何交错顺序下清理恰好执行一次，杜绝双重释放或泄漏。
/// 清理回调承担实际资源释放（FFmpeg 帧/解码上下文），状态机本身纯托管、可整体单测。
/// </summary>
/// <typeparam name="TPayload">交接的负载类型（持有需释放的资源）。</typeparam>
internal sealed class PrerollHandoff<TPayload>
    where TPayload : class
{
    /// <summary>交接状态：解码中 → 已完成 → 已收编；解码中被放弃 → 已放弃。</summary>
    private enum HandoffState
    {
        /// <summary>生产者解码中。</summary>
        Running,

        /// <summary>负载已交付、待消费者收编。</summary>
        Completed,

        /// <summary>消费者已收编（所有权移交消费者）。</summary>
        Taken,

        /// <summary>消费者已放弃（负载由持有方就近清理）。</summary>
        Abandoned,
    }

    private readonly object _gate = new();

    /// <summary>负载清理回调（释放原生资源）。</summary>
    private readonly Action<TPayload> _cleanup;

    private HandoffState _state = HandoffState.Running;

    private TPayload? _payload;

    /// <summary>创建交接。</summary>
    /// <param name="cleanup">负载清理回调：负责释放负载持有的全部原生资源。</param>
    public PrerollHandoff(Action<TPayload> cleanup) => _cleanup = cleanup;

    /// <summary>生产者交付负载。返回 false = 交接已被放弃或收编，负载已就地清理，调用方不得再触碰。</summary>
    /// <param name="payload">预解码负载。</param>
    /// <returns>true = 已保存待收编；false = 已被清理。</returns>
    public bool TryComplete(TPayload payload)
    {
        lock (_gate)
        {
            if (_state != HandoffState.Running)
            {
                _cleanup(payload);
                return false;
            }

            _payload = payload;
            _state = HandoffState.Completed;
            return true;
        }
    }

    /// <summary>消费者收编已完成的负载，所有权移交调用方（此后由调用方负责释放）。</summary>
    /// <param name="payload">收编的负载；失败时为 null。</param>
    /// <returns>true = 收编成功；false = 尚未完成或已被放弃。</returns>
    public bool TryTake(out TPayload payload)
    {
        lock (_gate)
        {
            if (_state == HandoffState.Completed)
            {
                payload = _payload!;
                _payload = null;
                _state = HandoffState.Taken;
                return true;
            }

            payload = null!;
            return false;
        }
    }

    /// <summary>消费者放弃交接：解码中 → 生产者完成时自清理；已完成 → 当场清理。可安全重复调用。</summary>
    public void Abandon()
    {
        lock (_gate)
        {
            switch (_state)
            {
                case HandoffState.Completed:
                    _cleanup(_payload!);
                    _payload = null;
                    _state = HandoffState.Abandoned;
                    break;
                case HandoffState.Running:
                    _state = HandoffState.Abandoned;
                    break;
            }
        }
    }
}
