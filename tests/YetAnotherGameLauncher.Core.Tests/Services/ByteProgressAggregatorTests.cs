using Xunit;
using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>
/// ByteProgressAggregator 直测（2026-10-02 二轮 review 补强：三条下载链共用的进度核心
/// 此前只经集成测试间接覆盖——节流语义与并发计数精确性单独钉住）。
/// </summary>
public class ByteProgressAggregatorTests
{
    [Fact]
    public void ZeroInterval_ReportsEveryDelta()
    {
        var frames = new List<long>();
        var aggregator = new ByteProgressAggregator(TimeSpan.Zero, frames.Add);
        var file = aggregator.CreateFileProgress();

        file.Report(10);
        file.Report(20);
        file.Report(20); // 无增量的重复累计值不投递

        Assert.Equal([10L, 20L], frames);
    }

    [Fact]
    public void LargeInterval_SwallowsIntermediateDeltas()
    {
        // 节流的意义：64KB 粒度回调不得逐帧洪泛 UI——interval 内的增量被吞，
        // 下一次过节流的投递携带的是全局累计（含被吞增量）。时间戳起点为 0，
        // 首帧必投（开局给 UI 立即反馈）——被节流的是后续帧
        var frames = new List<long>();
        var aggregator = new ByteProgressAggregator(TimeSpan.FromHours(1), frames.Add);
        var file = aggregator.CreateFileProgress();

        file.Report(10);
        Assert.Equal([10L], frames); // 首帧投递

        file.Report(20);
        Assert.Equal([10L], frames); // interval 内的后续增量被吞，不洪泛

        aggregator.ForceReport();
        Assert.Equal([10L, 20L], frames); // ForceReport 越过节流，携带最新累计（含被吞增量）
    }

    [Fact]
    public async Task ConcurrentFiles_AccountingIsExact()
    {
        // 并发面（一轮 review 曾仅推演）：并行文件各自换算 delta、完成时兜底补齐——
        // 无论回调交错如何，全局恰好 = Σ size，无丢失/重复计数
        var aggregator = new ByteProgressAggregator(TimeSpan.Zero, _ => { });
        const int files = 8, chunks = 20, chunkSize = 8;
        var progress = new IProgress<long>[files];
        for (var i = 0; i < files; i++)
        {
            progress[i] = aggregator.CreateFileProgress();
        }

        await Parallel.ForAsync(0, files, async (i, _) =>
        {
            for (var c = 1; c <= chunks; c++)
            {
                progress[i].Report(c * chunkSize);
                await Task.Yield();
            }
        });

        for (var i = 0; i < files; i++)
        {
            aggregator.Add(chunks * chunkSize - ((ByteProgressAggregator.FileProgress)progress[i]).ConsumedBytes);
        }

        Assert.Equal(files * chunks * (long)chunkSize, aggregator.Bytes);
    }
}
