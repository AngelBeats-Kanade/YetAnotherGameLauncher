using Xunit;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// <see cref="StubHttpHandler"/> 首请求门控的放行回归（2026-09-24 实锤死代码）：
/// SendAsync 命中门键后先 Remove 再 await——此后 ReleaseFirstRequest 按 URL 查键必得 null，
/// 放行从未生效过，被挂住的请求死等；"挂住→再放行"的确定性交错（资产代际窗口测试）从基建
/// 诞生起从未真正发生，依赖它的旧测试原绿是巧合（详见 GameItemAssetGenerationTests 头注释）。
/// 纯 HTTP 层测试，无 UI/无头依赖。
/// </summary>
public class StubHttpHandlerGateTests : IDisposable
{
    private readonly StubHttpHandler _handler = new();
    private readonly HttpClient _client;

    public StubHttpHandlerGateTests() => _client = new HttpClient(_handler);

    public void Dispose() => _client.Dispose();

    /// <summary>有界完成判定：ms 内完成返回响应，否则 null（红证据=超时后 null，而不是用例卡死）。</summary>
    private static async Task<HttpResponseMessage?> TryCompleteWithin(Task<HttpResponseMessage> request, int ms)
    {
        var winner = await Task.WhenAny(request, Task.Delay(ms));
        return ReferenceEquals(winner, request) && request.IsCompletedSuccessfully ? await request : null;
    }

    [Fact]
    public async Task ReleaseFirstRequest_UnblocksTheGatedRequest()
    {
        const string url = "https://cdn.example.com/pinned.png";
        _handler.Map(url, new byte[] { 1, 2, 3 });
        _ = _handler.GateFirstRequest(url);

        var gated = _client.GetAsync(url);
        await Task.Delay(100);
        Assert.False(gated.IsCompleted); // 已挂住（未放行前不得完成）

        _handler.ReleaseFirstRequest(url);

        // 红证据（修复前实测）：门键被 SendAsync Remove，放行找不到键，请求永远挂起
        var response = await TryCompleteWithin(gated, 2000);
        Assert.NotNull(response);
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task GateFirstRequest_OnlyFirstRequestHangs_OthersPassThrough()
    {
        const string url = "https://cdn.example.com/shared.png";
        _handler.Map(url, new byte[] { 4, 5, 6 });
        _ = _handler.GateFirstRequest(url);

        var first = _client.GetAsync(url);
        await Task.Delay(100);
        Assert.False(first.IsCompleted); // 首个请求挂住等放行

        // "首个"语义：门未放行期间，后续同 URL 请求直通（不被首个的挂起株连）
        var second = _client.GetAsync(url);
        var secondResponse = await TryCompleteWithin(second, 2000);
        Assert.NotNull(secondResponse);
        Assert.True(secondResponse.IsSuccessStatusCode);

        _handler.ReleaseFirstRequest(url);
        var firstResponse = await TryCompleteWithin(first, 2000);
        Assert.NotNull(firstResponse);
        Assert.True(firstResponse.IsSuccessStatusCode);
    }
}
