using Xunit;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 预卷交接状态机：完成/收编/放弃在任意交错顺序下，清理回调恰好执行一次、负载所有权不重叠。
/// 这是循环零间隙续播的资源安全根基（未收编的解码器与帧必须被释放且只释放一次）。
/// </summary>
public class PrerollHandoffTests
{
    /// <summary>记录清理调用次数的交接。</summary>
    private static (PrerollHandoff<string> Handoff, List<string> Cleaned) CreateHandoff()
    {
        var cleaned = new List<string>();
        return (new PrerollHandoff<string>(payload => cleaned.Add(payload)), cleaned);
    }

    [Fact]
    public void CompleteThenTake_TransfersOwnership_WithoutCleanup()
    {
        var (handoff, cleaned) = CreateHandoff();

        Assert.True(handoff.TryComplete("payload"));
        Assert.True(handoff.TryTake(out var payload));
        Assert.Equal("payload", payload);
        // 已收编：清理责任移交消费者，回调不得触发
        Assert.Empty(cleaned);
        // 二次收编失败（负载已移交）
        Assert.False(handoff.TryTake(out _));
        Assert.Empty(cleaned);
    }

    [Fact]
    public void TakeWhileRunning_Fails()
    {
        var (handoff, _) = CreateHandoff();

        Assert.False(handoff.TryTake(out var payload));
        Assert.Null(payload);
    }

    [Fact]
    public void AbandonWhileRunning_ProducerCompletionCleansPayload()
    {
        var (handoff, cleaned) = CreateHandoff();

        handoff.Abandon();
        // 生产者稍后完成：交接已放弃 → 负载就地清理、不得再被收编
        Assert.False(handoff.TryComplete("payload"));
        Assert.Equal(["payload"], cleaned);
        Assert.False(handoff.TryTake(out _));
    }

    [Fact]
    public void AbandonWhenCompleted_CleansImmediately()
    {
        var (handoff, cleaned) = CreateHandoff();
        handoff.TryComplete("payload");

        handoff.Abandon();

        Assert.Equal(["payload"], cleaned);
        Assert.False(handoff.TryTake(out _));
    }

    [Fact]
    public void AbandonTwice_CleansOnlyOnce()
    {
        var (handoff, cleaned) = CreateHandoff();
        handoff.TryComplete("payload");

        handoff.Abandon();
        handoff.Abandon();

        Assert.Equal(["payload"], cleaned);
    }

    [Fact]
    public void CompleteTwice_SecondPayloadIsRejectedAndCleaned()
    {
        var (handoff, cleaned) = CreateHandoff();

        Assert.True(handoff.TryComplete("first"));
        // 防御路径：正常生产者只完成一次，二次交付的负载应被清理
        Assert.False(handoff.TryComplete("second"));
        Assert.Equal(["second"], cleaned);

        // 首个负载不受影响，可正常收编
        Assert.True(handoff.TryTake(out var payload));
        Assert.Equal("first", payload);
        Assert.Equal(["second"], cleaned);
    }

    [Fact]
    public async Task CrossThreadHandoff_ProducerCompletes_ConsumerTakes()
    {
        // 模拟真实交错：生产者在另一任务里延迟交付，消费者等"交付已完成"信号再收编。
        // 不能依赖两段 Task.Delay 的相对时长——CI 高负载下线程池调度可能让生产者
        // 晚于消费者的固定等待结束才执行，TryTake 就会扑空（实锤过一次）
        var (handoff, cleaned) = CreateHandoff();

        var producer = Task.Run(async () =>
        {
            await Task.Delay(20);
            return handoff.TryComplete("payload");
        });

        Assert.True(await producer.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(handoff.TryTake(out var payload));
        Assert.Equal("payload", payload);
        Assert.Empty(cleaned);
    }
}
