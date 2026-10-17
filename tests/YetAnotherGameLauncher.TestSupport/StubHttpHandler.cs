using System.Net;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>
/// 测试用 HTTP 处理器：按 URL 返回预设字节，支持 Range 请求（bytes=N-），
/// 可模拟瞬态网络故障与"服务器忽略 Range"的行为。
/// </summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (byte[] Content, string? ContentType)> _responses = new(StringComparer.Ordinal);

    /// <summary>错位 206 的剩余配额（<see cref="Mismatched206FirstRequest"/> 的递减计数）。</summary>
    private int _mismatchedRequestsRemaining;

    /// <summary>已见过的 Range 请求数（配合 <see cref="Mismatched206AfterRequest"/> 决定从第几次开始错位）。</summary>
    private int _mismatchedSeen;

    /// <summary>错位配额的一次性初始化标志。</summary>
    private int _mismatchedQuotaInitialized;

    public List<HttpRequestMessage> Requests { get; } = [];

    public int FailFirstN { get; set; }

    /// <summary>前 N 次请求抛 TaskCanceledException（模拟连接/响应头超时，外部 token 未取消）。</summary>
    public int TimeoutFirstN { get; set; }

    public bool IgnoreRangeAndReturnFull { get; set; }

    /// <summary>前 N 次请求回 <see cref="FailWithStatusCode"/> 状态码（而非异常）——
    /// 408/429 等状态码分类测试用（真实走 EnsureSuccessStatusCode 的生产路径）。</summary>
    public int FailWithStatusFirstN { get; set; }

    /// <summary><see cref="FailWithStatusFirstN"/> 生效时返回的状态码（默认 408）。</summary>
    public HttpStatusCode FailWithStatusCode { get; set; } = HttpStatusCode.RequestTimeout;

    /// <summary>强制 206 响应声称的 Range 起始字节（F64：模拟畸形服务器对 Range 请求
    /// 回错误起点的 206；null = 按请求范围正常回）。</summary>
    public long? ForcedRangeStart { get; set; }

    /// <summary>先放过前 N 次 Range 请求（照常对齐），此后按 <see cref="Mismatched206FirstRequest"/> 的配额错位：
    /// 用于把错位落在"探测之后的某次数据读取"上（对齐守卫在 RemoteZipEntryReader.ReadAsync 里，
    /// 只错位探测请求到不了那里）。</summary>
    public int Mismatched206AfterRequest { get; set; }

    /// <summary>仅前 N 次 Range 请求回错位 206（起始字节取 <see cref="ForcedRangeStart"/>），
    /// 其后恢复正常（F64 恢复腿）；null = 恒定错位。</summary>
    public int? Mismatched206FirstRequest { get; set; }

    public void Map(string url, byte[] content, string? contentType = null) => _responses[url] = (content, contentType);

    public void Map(string url, string content, string? contentType = null) =>
        Map(url, System.Text.Encoding.UTF8.GetBytes(content), contentType);

    private readonly Dictionary<string, FirstRequestGate> _firstRequestGates = new(StringComparer.Ordinal);

    /// <summary>首个请求门：门键常驻字典（不 Remove——Remove 后 ReleaseFirstRequest 按 URL
    /// 查键必得 null，放行从未生效过，2026-09-24 实锤死代码）；首个到达者认领并挂住等放行，
    /// 后续请求直通（"首个"语义）。</summary>
    private sealed class FirstRequestGate
    {
        /// <summary>放行信号。</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>认领标志：0=未认领，1=已被首个到达者认领。</summary>
        public int Claimed;
    }

    /// <summary>让发往该 URL 的第一个请求挂起，直到 <see cref="ReleaseFirstRequest"/>；
    /// 后续请求直通。竞态测试用它把调用链钉在真实 await 点上构造确定性交错。
    /// 同一 URL 重复调用会覆盖旧门（旧等待者失去放行路径而永挂）——现无此用法，
    /// 新用例须保证每个 URL 只门一次（N4，2026-09-24 review 立项）。</summary>
    public Task GateFirstRequest(string url)
    {
        var gate = new FirstRequestGate();
        _firstRequestGates[url] = gate;
        return gate.Release.Task;
    }

    /// <summary>放行被 <see cref="GateFirstRequest"/> 挂住的第一个请求。</summary>
    public void ReleaseFirstRequest(string url) => _firstRequestGates.GetValueOrDefault(url)?.Release.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (_firstRequestGates.TryGetValue(request.RequestUri!.ToString(), out var gate))
        {
            // 门键不 Remove（放行要按 URL 找回门键）；首个到达者认领挂住，后来者直通。
            // 已放行（Release.Task.IsCompleted）时认领立即通过——先放行后请求同样直通
            if (Interlocked.CompareExchange(ref gate.Claimed, 1, 0) == 0)
            {
                await gate.Release.Task.ConfigureAwait(false);
            }
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

        if (FailWithStatusFirstN > 0)
        {
            // 前 N 次请求回指定状态码（408/429 等瞬态分类测试：真实状态码走 EnsureSuccessStatusCode
            // 抛 HttpRequestException 的生产路径，而非直接抛异常）
            FailWithStatusFirstN--;
            return new HttpResponseMessage(FailWithStatusCode) { Content = new ByteArrayContent([]) };
        }

        var url = request.RequestUri!.ToString();
        if (!_responses.TryGetValue(url, out var mapped))
        {
            // 带 cache-buster 时间戳（…switch.json?_t=…）等动态 query 的请求：回退按无 query 的 URL 匹配
            var query = request.RequestUri.Query;
            var bare = query.Length > 0 ? url[..^query.Length] : null;
            if (bare is null || !_responses.TryGetValue(bare, out mapped))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        var (content, contentType) = mapped;

        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        var ranged = range is not null && !IgnoreRangeAndReturnFull;
        // 忠实 Range 语义：From/To 都照做（To 缺省到内容末尾），suffix 形态（bytes=-N）取末尾 N 字节。
        // 服务器忽略 Range 的形态由 IgnoreRangeAndReturnFull 显式模拟（200 + 全量）。
        var start = ranged
            ? (int)(range!.From ?? Math.Max(0, content.Length - (range.To ?? 0)))
            : 0;
        var endExclusive = ranged
            ? (int)Math.Min(content.Length, (range!.From is null ? content.Length : (range.To ?? content.Length - 1) + 1))
            : content.Length;
        var forcedMismatch = false;
        if (ranged && ForcedRangeStart is { } forced)
        {
            // 错位配额一次性初始化（恒定错位 = 无限配额），此后每次 Range 请求递减
            if (Interlocked.CompareExchange(ref _mismatchedQuotaInitialized, 1, 0) == 0)
            {
                _mismatchedRequestsRemaining = Mismatched206FirstRequest ?? int.MaxValue;
            }

            var seen = Interlocked.Increment(ref _mismatchedSeen);
            var misaligned = seen > Mismatched206AfterRequest
                             && Interlocked.Decrement(ref _mismatchedRequestsRemaining) >= 0;
            if (misaligned)
            {
                var requestedStart = (int)(range!.From ?? 0);
                start = (int)forced;
                endExclusive = content.Length; // 错位形态按既有测试语义回"从错位起点到末尾"
                forcedMismatch = start != requestedStart;
            }
        }

        if (ranged && start >= content.Length)
        {
            // 规范服务器行为：Range 起点不小于内容长度时整个范围不可满足，回 416
            return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
        }

        var slice = start == 0 && endExclusive == content.Length ? content : content[start..endExclusive];
        // 状态码与实体必须自洽（复审非阻塞 8，2026-10-17 修）：只发了子集就必须 206 + Content-Range，
        // 200 只能配全量体——旧逻辑按 start > 0 判状态，bytes=0-N 会回"200 + 截断体"，
        // 读取器把它当整包缓存，随后"读首条目（本地头偏移 0）"被误判越界（假红陷阱）。
        // F64：错位形态同样强制 206（携带错位 Content-Range）——否则下载器按"服务器忽略 Range 重写"
        // 处理，畸形 206 防线永远不可达、测试空心
        var isPartial = slice.Length != content.Length;
        var response = new HttpResponseMessage((ranged && isPartial) || forcedMismatch
            ? HttpStatusCode.PartialContent
            : HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(slice),
        };
        if (ranged && isPartial && !forcedMismatch)
        {
            // 忠实 206：对齐的 Content-Range 带上总长度（下载器与远程条目读取器按它定位）
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(start, start + slice.Length - 1, content.Length)
                {
                    Unit = "bytes",
                };
        }
        else if (ranged && ForcedRangeStart is { } forcedStart)
        {
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(forcedStart, content.Length - 1)
                {
                    Unit = "bytes",
                };
        }

        if (contentType is not null)
        {
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        return response;
    }
}
