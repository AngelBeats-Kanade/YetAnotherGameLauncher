using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 一次操作专用的进度转发器（串行投递 + 代际门控），替代裸 <see cref="Progress{T}"/>：
/// <list type="bullet">
/// <item>串行 + 保序：有同步上下文（生产 = UI 线程上的 Avalonia 派发器）时按上下文 FIFO 投递，
/// 回调内再持锁；无上下文（无头测试）时就地持锁串行执行。裸 <c>Progress&lt;T&gt;</c> 在没有
/// SynchronizationContext 的环境会退化为线程池投递——回调并发执行且不保序，迟到的
/// <see cref="UpdatePhase.Downloading"/> 报告能把已经落地的终值覆盖回中间态（2026-10-18 F55
/// 探针实锤：UI 套件偶发红 = 稳定期轮询等满 10 s，Actual 停在下载文案）。</item>
/// <item>代际门控：操作结束（代际前移）之后，属于旧代际的迟到报告一律丢弃——已完成操作的状态
/// 不再被改写（生产端被派发器延后的报告同样适用）。</item>
/// </list>
/// </summary>
/// <param name="handler">应用一个进度报告（VM 的状态写入）。</param>
/// <param name="generation">本操作开启时的代际号。</param>
/// <param name="currentGeneration">读取当前代际（失效判定）。</param>
/// <param name="context">创建时的同步上下文；null = 就地串行投递。</param>
internal sealed class OperationProgress(
    Action<UpdateProgress> handler,
    int generation,
    Func<int> currentGeneration,
    SynchronizationContext? context = null) : IProgress<UpdateProgress>
{
    private readonly object _gate = new();

    /// <summary>投递一个进度报告（可来自任意线程；按创建顺序串行应用，且仅当代际仍有效）。</summary>
    public void Report(UpdateProgress value)
    {
        if (context is null)
        {
            lock (_gate)
            {
                Apply(value);
            }

            return;
        }

        context.Post(
            _ =>
            {
                lock (_gate)
                {
                    Apply(value);
                }
            },
            null);
    }

    private void Apply(UpdateProgress value)
    {
        if (currentGeneration() == generation)
        {
            handler(value);
        }
    }
}
