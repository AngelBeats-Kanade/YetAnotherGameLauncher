using Xunit;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 进度转发器的两条硬语义（F55 根因修复，2026-10-18）：
/// ①无同步上下文（无头测试）时回调串行执行——裸 Progress&lt;T&gt; 会退化为线程池并发投递，
/// 回调乱序（探针实锤：迟到的 Downloading 报告把终值"完成"覆盖回下载文案）；
/// ②代际失效后迟到报告一律丢弃——操作结束后的报告不得改写已完成操作的状态。
/// </summary>
public class OperationProgressTests
{
    private static UpdateProgress Report(long bytes) =>
        new(UpdatePhase.Downloading, 100, bytes, 0, 1, null);

    [Fact]
    public async Task Report_WithoutContext_RunsCallbacksSerially()
    {
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var concurrent = 0;
        var maxConcurrent = 0;
        var progress = new OperationProgress(
            report =>
            {
                var running = Interlocked.Increment(ref concurrent);
                maxConcurrent = Math.Max(maxConcurrent, running);
                if (report.DownloadedBytes == 1)
                {
                    firstEntered.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
                else
                {
                    secondEntered.Set();
                }

                Interlocked.Decrement(ref concurrent);
            },
            generation: 0,
            currentGeneration: () => 0);

        var first = Task.Run(() => progress.Report(Report(1)));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
        var second = Task.Run(() => progress.Report(Report(2)));

        // 第一条回调仍阻塞在临界区内时，第二条必须进不来（裸 Progress<T> 在这里并发进入）
        Assert.False(secondEntered.Wait(TimeSpan.FromMilliseconds(200)));
        release.Set();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, maxConcurrent);
        Assert.True(secondEntered.IsSet);
    }

    [Fact]
    public void Report_AfterGenerationBump_IsDropped()
    {
        var applied = new List<long>();
        var generation = 0;
        var progress = new OperationProgress(
            p => applied.Add(p.DownloadedBytes),
            generation: generation,
            currentGeneration: () => Volatile.Read(ref generation));

        progress.Report(Report(1));
        Interlocked.Increment(ref generation); // 操作结束：代际前移
        progress.Report(Report(2));            // 迟到报告

        Assert.Equal([1L], applied);
    }

    [Fact]
    public void Report_AfterGenerationBump_FromOtherThreads_NeverApplies()
    {
        var applied = 0;
        var generation = 0;
        var progress = new OperationProgress(
            _ => Interlocked.Increment(ref applied),
            generation: 0,
            currentGeneration: () => Volatile.Read(ref generation));

        Interlocked.Increment(ref generation);
        Parallel.For(0, 32, _ => progress.Report(Report(1)));

        Assert.Equal(0, applied);
    }
}
