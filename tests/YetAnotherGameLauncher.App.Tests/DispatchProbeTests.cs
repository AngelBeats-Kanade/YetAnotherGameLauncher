using Xunit;
using YetAnotherGameLauncher.AppTests;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// Phase 0 探针（2026-09-19）：实测 Avalonia 12.1.2 HeadlessUnitTestSession.Dispatch 各重载
/// 对 lambda 内断言失败的传播行为。三个探针按设计都必须失败——任何一个显示"通过"即证明
/// 该重载会吞断言（假绿）。结论写入 artifacts/audit/phase0/，Phase 2 将转正为常驻哨兵测试。
/// Assert.Fail 与其他 Assert.* 失败时抛同一种 FailException，传播行为完全等价。
/// </summary>
[Collection("sequential")]
public class DispatchProbeTests
{
    [Fact]
    public async Task Probe1_ActionOverload_AssertFailureInside_MustFail()
    {
        await HeadlessSession.Instance.Dispatch(() =>
        {
            Assert.Fail("探针1：Action 重载内断言失败必须传播到调用方");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Probe2_FuncTaskOverload_AssertFailureBeforeAwait_MustFail()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            Assert.Fail("探针2：Func<Task> 重载 await 之前断言失败必须传播到调用方");
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Probe3_FuncTaskOverload_AssertFailureAfterRealAwait_MustFail()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            await Task.Delay(10);
            Assert.Fail("探针3：Func<Task> 重载真 await 之后断言失败必须传播到调用方");
        }, CancellationToken.None);
    }
}
