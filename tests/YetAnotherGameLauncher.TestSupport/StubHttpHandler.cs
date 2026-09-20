using System.Net;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>
/// 测试用 HTTP 处理器：按 URL 返回预设字节，支持 Range 请求（bytes=N-），
/// 可模拟瞬态网络故障与"服务器忽略 Range"的行为。
/// </summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _responses = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    public int FailFirstN { get; set; }

    /// <summary>前 N 次请求抛 TaskCanceledException（模拟连接/响应头超时，外部 token 未取消）。</summary>
    public int TimeoutFirstN { get; set; }

    public bool IgnoreRangeAndReturnFull { get; set; }

    public void Map(string url, byte[] content) => _responses[url] = content;

    public void Map(string url, string content) => Map(url, System.Text.Encoding.UTF8.GetBytes(content));

    private readonly Dictionary<string, TaskCompletionSource> _firstRequestGates = new(StringComparer.Ordinal);

    /// <summary>让发往该 URL 的第一个请求挂起，直到 <see cref="ReleaseFirstRequest"/>；
    /// 后续请求直通。竞态测试用它把调用链钉在真实 await 点上构造确定性交错。</summary>
    public Task GateFirstRequest(string url) =>
        (_firstRequestGates[url] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    /// <summary>放行被 <see cref="GateFirstRequest"/> 挂住的第一个请求。</summary>
    public void ReleaseFirstRequest(string url) => _firstRequestGates.GetValueOrDefault(url)?.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (_firstRequestGates.TryGetValue(request.RequestUri!.ToString(), out var gate))
        {
            _firstRequestGates.Remove(request.RequestUri.ToString());
            await gate.Task.ConfigureAwait(false);
        }

        if (FailFirstN > 0)
        {
            FailFirstN--;
            throw new HttpRequestException("模拟瞬态网络故障");
        }

        if (TimeoutFirstN > 0)
        {
            TimeoutFirstN--;
            throw new TaskCanceledException("模拟请求超时（HttpClient.Timeout）");
        }

        var url = request.RequestUri!.ToString();
        if (!_responses.TryGetValue(url, out var content))
        {
            // 带 cache-buster 时间戳（…switch.json?_t=…）等动态 query 的请求：回退按无 query 的 URL 匹配
            var query = request.RequestUri.Query;
            var bare = query.Length > 0 ? url[..^query.Length] : null;
            if (bare is null || !_responses.TryGetValue(bare, out content))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        var ranged = range is not null && !IgnoreRangeAndReturnFull;
        var start = (int)(ranged ? range!.From ?? 0 : 0);

        if (ranged && start >= content.Length)
        {
            // 规范服务器行为：Range 起点不小于内容长度时整个范围不可满足，回 416
            return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
        }

        var slice = start == 0 ? content : content[start..];
        var response = new HttpResponseMessage(ranged && start > 0
            ? HttpStatusCode.PartialContent
            : HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(slice),
        };
        return response;
    }
}
