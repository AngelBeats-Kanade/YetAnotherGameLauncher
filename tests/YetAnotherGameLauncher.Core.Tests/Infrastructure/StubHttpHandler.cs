using System.Net;

namespace YetAnotherGameLauncher.Core.Tests.Infrastructure;

/// <summary>
/// 测试用 HTTP 处理器：按 URL 返回预设字节，支持 Range 请求（bytes=N-），
/// 可模拟瞬态网络故障与"服务器忽略 Range"的行为。
/// </summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _responses = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    public int FailFirstN { get; set; }

    public bool IgnoreRangeAndReturnFull { get; set; }

    public void Map(string url, byte[] content) => _responses[url] = content;

    public void Map(string url, string content) => Map(url, System.Text.Encoding.UTF8.GetBytes(content));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (FailFirstN > 0)
        {
            FailFirstN--;
            throw new HttpRequestException("模拟瞬态网络故障");
        }

        var url = request.RequestUri!.ToString();
        if (!_responses.TryGetValue(url, out var content))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        var ranged = range is not null && !IgnoreRangeAndReturnFull;
        var start = (int)(ranged ? range!.From ?? 0 : 0);

        var slice = start == 0 ? content : content[start..];
        var response = new HttpResponseMessage(ranged && start > 0
            ? HttpStatusCode.PartialContent
            : HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(slice),
        };
        return Task.FromResult(response);
    }
}
