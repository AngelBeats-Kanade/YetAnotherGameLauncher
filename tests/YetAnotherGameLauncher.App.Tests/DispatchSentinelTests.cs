using Xunit;
using Xunit.Sdk;
using YetAnotherGameLauncher.AppTests;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 常驻哨兵（原 Phase 0 探针转正，2026-09-19）：锁定 HeadlessUnitTestSession.Dispatch 的
/// 「同步 lambda 内异常必须传播到 await 调用方」这一属性。Avalonia/xunit 升级若引入吞断言行为
/// （如同版本 Func&lt;Task&gt; 重载的实锤吞断言），本哨兵立即变红。
/// 注意：Dispatch(Func&lt;Task&gt;)（async lambda）全形态吞断言已实测实锤（规则见 AGENTS.md「Dispatch 三规则」），
/// 属禁用形态，无法用测试守卫"不吞"——由 AGENTS.md 纪律 + CI grep `Dispatch(async` 兜底。
/// </summary>
[Collection("sequential")]
public class DispatchSentinelTests
{
    [Fact]
    public async Task Dispatch_ActionOverload_PropagatesAssertionFailures()
    {
        await Assert.ThrowsAsync<FailException>(() => HeadlessSession.Instance.Dispatch(() =>
        {
            Assert.Fail("哨兵：该异常必须传播到调用方（吞掉 = runner/框架升级引入假绿回归）");
        }, CancellationToken.None));
    }

    [Fact]
    public async Task Dispatch_ActionOverload_PropagatesArbitraryExceptions()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => HeadlessSession.Instance.Dispatch(() =>
            throw new InvalidOperationException("哨兵：任意异常也必须传播"), CancellationToken.None));
    }
}
