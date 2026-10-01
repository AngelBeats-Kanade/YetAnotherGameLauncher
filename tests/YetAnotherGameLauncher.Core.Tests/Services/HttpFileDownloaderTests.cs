using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class HttpFileDownloaderTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly StubHttpHandler _handler = new();
    private readonly HttpClient _client;

    public HttpFileDownloaderTests()
    {
        _client = new HttpClient(_handler);
    }

    public void Dispose()
    {
        _client.Dispose();
        _tempDir.Dispose();
    }

    private const string Url = "https://cdn.example.com/file.bin";
    private static readonly byte[] Content = "hello launcher download content"u8.ToArray();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpFileDownloader CreateDownloader(int maxAttempts = 3) => new(_client, new HttpFileDownloaderOptions
    {
        MaxAttempts = maxAttempts,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
    });

    private DownloadRequest Request(
        string? fileName = null,
        long? expectedSize = null,
        string? expectedMd5 = null) => new(
        Url,
        fileName is null ? _tempDir.FilePath("file.bin") : _tempDir.FilePath(fileName),
        expectedSize,
        expectedMd5);

    [Fact]
    public async Task DownloadFileAsync_WritesExactContent()
    {
        _handler.Map(Url, Content);

        await CreateDownloader().DownloadFileAsync(Request(), cancellationToken: Ct);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_tempDir.FilePath("file.bin")));
    }

    [Fact]
    public async Task DownloadFileAsync_CreatesDestinationDirectory()
    {
        _handler.Map(Url, Content);

        var request = new DownloadRequest(Url, _tempDir.FilePath("sub", "dir", "file.bin"), null, null);

        await CreateDownloader().DownloadFileAsync(request, cancellationToken: Ct);

        Assert.True(File.Exists(_tempDir.FilePath("sub", "dir", "file.bin")));
    }

    [Fact]
    public async Task DownloadFileAsync_SizeMatch_Succeeds()
    {
        _handler.Map(Url, Content);

        await CreateDownloader().DownloadFileAsync(Request(expectedSize: Content.Length), cancellationToken: Ct);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_tempDir.FilePath("file.bin")));
    }

    [Fact]
    public async Task DownloadFileAsync_SizeMismatch_ThrowsVerificationException()
    {
        _handler.Map(Url, Content);

        var ex = await Assert.ThrowsAsync<DownloadVerificationException>(
            () => CreateDownloader().DownloadFileAsync(Request(expectedSize: Content.Length + 100), cancellationToken: Ct));

        Assert.Contains("Size mismatch", ex.Message);
        Assert.False(File.Exists(_tempDir.FilePath("file.bin")));
        Assert.False(File.Exists(_tempDir.FilePath("file.bin.temp")));
    }

    [Fact]
    public async Task DownloadFileAsync_Md5Match_Succeeds()
    {
        _handler.Map(Url, Content);
        var md5 = Hashing.Md5Hex(Content);

        await CreateDownloader().DownloadFileAsync(Request(expectedMd5: md5), cancellationToken: Ct);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_tempDir.FilePath("file.bin")));
    }

    [Fact]
    public async Task DownloadFileAsync_Md5Mismatch_RetriesFromScratchThenThrows()
    {
        _handler.Map(Url, Content);

        var ex = await Assert.ThrowsAsync<DownloadVerificationException>(
            () => CreateDownloader(maxAttempts: 3).DownloadFileAsync(
                Request(expectedMd5: "deadbeefdeadbeefdeadbeefdeadbeef"), cancellationToken: Ct));

        Assert.Contains("MD5", ex.Message);
        // 每次校验失败后删除临时文件并从头重试
        Assert.Equal(3, _handler.Requests.Count);
        Assert.False(File.Exists(_tempDir.FilePath("file.bin")));
    }

    [Fact]
    public async Task DownloadFileAsync_EmptyExpectedMd5_IsNoInfo_SkipsMd5Check()
    {
        // 回归（2026-09-20）：渠道清单缺失 md5 字段时上游传空串——空串是"无校验信息"，
        // 不是校验目标；旧语义任何真实内容都恒不匹配，大包按校验失败重下到重试耗尽
        _handler.Map(Url, Content);

        await CreateDownloader().DownloadFileAsync(Request(expectedMd5: ""), cancellationToken: Ct);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_tempDir.FilePath("file.bin")));
    }

    [Fact]
    public async Task DownloadFileAsync_ZeroExpectedSize_IsNoInfo_SkipsSizeCheck()
    {
        // 同上：Gryphline 包尺寸解析失败回退 0——0 是"无尺寸信息"，非零真实内容不得被判尺寸不符
        _handler.Map(Url, Content);

        await CreateDownloader().DownloadFileAsync(Request(expectedSize: 0), cancellationToken: Ct);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_tempDir.FilePath("file.bin")));
    }

    [Fact]
    public async Task DownloadFileAsync_ResumesFromTempFile()
    {
        var half = Content[..(Content.Length / 2)];
        var tempPath = _tempDir.FilePath("file.bin.temp");
        await File.WriteAllBytesAsync(tempPath, half);
        _handler.Map(Url, Content);

        await CreateDownloader().DownloadFileAsync(Request(), cancellationToken: Ct);

        // 发出了带 Range 的续传请求
        var rangeHeader = Assert.Single(_handler.Requests).Headers.Range;
        Assert.NotNull(rangeHeader);
        Assert.Equal(half.Length, Assert.Single(rangeHeader.Ranges).From);
        // 最终文件是完整内容
        Assert.Equal(Content, await File.ReadAllBytesAsync(_tempDir.FilePath("file.bin")));
        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public async Task DownloadFileAsync_ServerIgnoresRange_RestartsFromZero()
    {
        var half = Content[..(Content.Length / 2)];
        await File.WriteAllBytesAsync(_tempDir.FilePath("file.bin.temp"), half);
        _handler.Map(Url, Content);
        _handler.IgnoreRangeAndReturnFull = true;

        await CreateDownloader().DownloadFileAsync(Request(), cancellationToken: Ct);

        // 服务器返回 200 全量：临时文件被丢弃重写，最终仍是完整内容
        Assert.Equal(Content, await File.ReadAllBytesAsync(_tempDir.FilePath("file.bin")));
    }

    [Fact]
    public async Task DownloadFileAsync_ZeroSizeExpected_DownloadsEmptyFile()
    {
        // 回归：ExpectedSize=0 且 .temp 不存在时不得走"temp 已完整"捷径——
        // 那会跳过下载直接对不存在的 temp 做校验抛 FileNotFoundException
        _handler.Map(Url, []);

        await CreateDownloader().DownloadFileAsync(Request(expectedSize: 0), cancellationToken: Ct);

        Assert.True(File.Exists(_tempDir.FilePath("file.bin")));
        Assert.Empty(await File.ReadAllBytesAsync(_tempDir.FilePath("file.bin")));
    }

    [Fact]
    public async Task DownloadFileAsync_CompleteTempWithExpectedSize_PlacesWithoutAnyRequest()
    {
        // 回归：.temp 已达期望尺寸（下载完、落盘前退出，或目标被占用后重试）时，
        // 续传请求不可满足会被规范服务器回 416 → 误分类为网络错误 → 重试耗尽成死路。
        // 现应跳过请求直接校验落盘。
        await File.WriteAllBytesAsync(_tempDir.FilePath("file.bin.temp"), Content);
        _handler.Map(Url, Content);

        await CreateDownloader().DownloadFileAsync(
            Request(expectedSize: Content.Length, expectedMd5: Hashing.Md5Hex(Content)), cancellationToken: Ct);

        Assert.Empty(_handler.Requests); // 零网络请求
        Assert.Equal(Content, await File.ReadAllBytesAsync(_tempDir.FilePath("file.bin")));
    }

    [Fact]
    public async Task DownloadFileAsync_ServerRejectsResumeWith416_DeletesTempAndRestartsFromScratch()
    {
        // 远端内容比 .temp 还短（换资源/缩水）：416 后按校验失败丢弃 .temp 从零重下，而非死路
        var stale = "stale-temp-content-longer-than-remote-content"u8.ToArray();
        await File.WriteAllBytesAsync(_tempDir.FilePath("file.bin.temp"), stale);
        var fresh = "fresh"u8.ToArray();
        _handler.Map(Url, fresh);

        await CreateDownloader().DownloadFileAsync(Request(), cancellationToken: Ct);

        Assert.Equal(2, _handler.Requests.Count); // 首次带 Range 收 416，随后从零重下
        Assert.NotNull(_handler.Requests[0].Headers.Range);
        Assert.Null(_handler.Requests[1].Headers.Range);
        Assert.Equal(fresh, await File.ReadAllBytesAsync(_tempDir.FilePath("file.bin")));
        Assert.False(File.Exists(_tempDir.FilePath("file.bin.temp")));
    }

    [Fact]
    public async Task DownloadFileAsync_Mismatched206RangeStart_DiscardsTempAndRedownloads()
    {
        // F64：206 起始字节与请求不符（畸形服务器）时原实现照走续传——错位数据拼进 .temp，
        // 无 MD5/size 清单时**静默落盘损坏文件**。修复 = 206 声称的 From ≠ 请求起点丢弃
        // .temp 重下。错位只发生在带 Range 的续传请求上（删 temp 后的无 Range 请求必得
        // 干净 200），因此自愈路径恒可达、不存在"重试耗尽"形态。
        // 变异自查：击穿起点校验后本用例红（Actual=损坏字节落盘），防线真可达
        await File.WriteAllBytesAsync(_tempDir.FilePath("file.bin.temp"), "CORRUPT"u8.ToArray());
        _handler.ForcedRangeStart = 0; // Range 请求回 bytes=0- 的错位 206
        _handler.Mismatched206FirstRequest = 1; // 仅首次错位（其后 Range 请求正常——本用例第二次请求已不带 Range）
        _handler.Map(Url, Content);

        await CreateDownloader().DownloadFileAsync(Request(), cancellationToken: Ct);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_tempDir.FilePath("file.bin")));
        Assert.False(File.Exists(_tempDir.FilePath("file.bin.temp")));
    }

    [Fact]
    public async Task DownloadFileAsync_TempWriteFailure_TerminalSingleAttemptWithLocalReason()
    {
        // F65：本地写故障（.temp 落点被目录占用/磁盘满/只读）不是网络瞬态——原实现把一切
        // IOException 按网络错误重试 MaxAttempts 次（重下多少遍都不会好），真实原因沉底；
        // .temp 只读的 UnauthorizedAccessException 甚至裸穿无分类。写盘路径单独折算：
        // 单次终止、消息指向本地文件而非 URL。复现形态：.temp 位置被同名目录占用
        // （Linux 开目录写 = EISDIR/IOException，Windows = UnauthorizedAccessException）
        Directory.CreateDirectory(_tempDir.FilePath("file.bin.temp"));
        _handler.Map(Url, Content);

        var ex = await Assert.ThrowsAsync<DownloadException>(
            () => CreateDownloader().DownloadFileAsync(Request(), cancellationToken: Ct));

        Assert.DoesNotContain("Download failed (", ex.Message); // 不按网络瞬态包装
        Assert.Contains("temp file", ex.Message, StringComparison.Ordinal);
        Assert.Single(_handler.Requests); // 不重试
    }

    [Fact]
    public async Task DownloadFileAsync_RetriesTransientFailures()
    {
        _handler.Map(Url, Content);
        _handler.FailFirstN = 1;

        await CreateDownloader(maxAttempts: 3).DownloadFileAsync(Request(), cancellationToken: Ct);

        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal(Content, await File.ReadAllBytesAsync(_tempDir.FilePath("file.bin")));
    }

    [Fact]
    public async Task DownloadFileAsync_RetriesExhausted_ThrowsDownloadException()
    {
        _handler.FailFirstN = 10;

        var ex = await Assert.ThrowsAsync<DownloadException>(
            () => CreateDownloader(maxAttempts: 3).DownloadFileAsync(Request(), cancellationToken: Ct));

        Assert.Contains(Url, ex.Message);
        Assert.Equal(3, _handler.Requests.Count);
        Assert.False(File.Exists(_tempDir.FilePath("file.bin")));
    }

    [Fact]
    public async Task DownloadFileAsync_ReportsMonotonicProgress()
    {
        _handler.Map(Url, Content);
        // 进度契约断言的是"签发顺序单调"。Progress<T> 的投递（SynchronizationContext.Post /
        // 线程池派发）不保证与签发同序：早前形态直接对投递队列断言相邻单调，SpinUntil 见到
        // 末值即退出、更早的报告可能仍在途，偶发假红（e5d202d 引入断言后 7 实跑 1 红，
        // 2026-09-26 本会话复现 20 跑 2 红）——改为 OnReport 重载在签发线程同步记账：
        // await 完成后全部报告必然已入列，列表顺序即签发顺序，零竞态
        var reports = new IssueOrderProgress();

        await CreateDownloader().DownloadFileAsync(Request(), reports, Ct);

        Assert.NotEmpty(reports.Issued);
        Assert.Equal(Content.Length, reports.Issued.Last());
        Assert.Equal(reports.Issued.OrderBy(b => b), reports.Issued); // 签发序列不得回退
    }

    /// <summary>在签发线程同步记账的 <see cref="Progress{T}"/>（OnReport 由 Report 的调用
    /// 线程直接执行，不经异步投递），供"签发顺序"类断言使用。</summary>
    private sealed class IssueOrderProgress : Progress<long>
    {
        private readonly object _gate = new();

        public List<long> Issued { get; } = [];

        protected override void OnReport(long value)
        {
            lock (_gate)
            {
                Issued.Add(value);
            }
        }
    }

    [Fact]
    public async Task DownloadFileAsync_ProgressOnResume_ReportsTotalIncludingResumedBytes()
    {
        var half = Content[..(Content.Length / 2)];
        await File.WriteAllBytesAsync(_tempDir.FilePath("file.bin.temp"), half);
        _handler.Map(Url, Content);
        var reports = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var progress = new Progress<long>(reports.Enqueue);

        await CreateDownloader().DownloadFileAsync(Request(), progress, Ct);

        // 等待最终报告送达；续传时所有报告的字节数都应包含已存在的临时文件部分
        Assert.True(SpinWait.SpinUntil(
            () => reports.Contains(Content.Length), TimeSpan.FromSeconds(5)));
        Assert.Equal(Content.Length, reports.Max());
        Assert.All(reports, bytes => Assert.True(bytes >= half.Length));
    }

    [Fact]
    public async Task DownloadFileAsync_OverwritesExistingFile()
    {
        var oldContent = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(_tempDir.FilePath("file.bin"), oldContent);
        _handler.Map(Url, Content);

        await CreateDownloader().DownloadFileAsync(Request(), cancellationToken: Ct);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_tempDir.FilePath("file.bin")));
    }

    [Fact]
    public async Task DownloadFileAsync_Cancelled_ThrowsOperationCanceled()
    {
        _handler.Map(Url, Content);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateDownloader().DownloadFileAsync(Request(), cancellationToken: cts.Token));
    }
}

public class HttpFileDownloaderTimeoutTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly StubHttpHandler _handler = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public async Task Download_RetriesTimeoutException_WhenCallerTokenNotCancelled()
    {
        // 回归（2026-09-20 复审）：连接/响应头超时抛 TaskCanceledException（外部 token 未取消），
        // 必须按瞬态网络错误重试而非上抛裸 OCE——消费端把一切 OCE 当"用户取消"静默吞，
        // 弱网下更新会无声中断且无失败提示（SystemProcessRunner 同款教训）
        _handler.TimeoutFirstN = 1;
        _handler.Map("https://cdn.example.com/file.bin", "timeout-retry-payload"u8.ToArray());
        using var client = new HttpClient(_handler);
        var downloader = new HttpFileDownloader(client, new HttpFileDownloaderOptions
        {
            MaxAttempts = 3,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        });

        await downloader.DownloadFileAsync(new DownloadRequest(
            "https://cdn.example.com/file.bin", _tempDir.FilePath("file.bin"), null, null));

        Assert.Equal(
            "timeout-retry-payload",
            await File.ReadAllTextAsync(_tempDir.FilePath("file.bin")));
    }
}

public class HttpFileDownloaderLockedDestinationTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly StubHttpHandler _handler = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public async Task Download_DestinationLocked_SingleAttemptClassifiedAsReplaceFailure()
    {
        // 回归（2026-09-20 三审防线守卫）：目标被占用（Windows 语义）单独分类为不可重试的
        // DownloadException——该分支若被并入网络重试 catch，重下多少遍都不会好
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("exclusive file locks blocking rename are Windows-only semantics");
        }

        _handler.Map("https://cdn.example.com/file.bin", "payload"u8.ToArray());
        var destination = _tempDir.FilePath("file.bin");
        using var lockHandle = File.Open(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var client = new HttpClient(_handler);
        var downloader = new HttpFileDownloader(client, new HttpFileDownloaderOptions
        {
            MaxAttempts = 3,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        });

        var ex = await Assert.ThrowsAsync<DownloadException>(
            () => downloader.DownloadFileAsync(new DownloadRequest(
                "https://cdn.example.com/file.bin", destination, null, null)));

        Assert.Contains("replace", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(_handler.Requests); // 不进网络重试：单次请求即终止
    }

    [Fact]
    public async Task Download_NonSuccessStatusCode_SingleAttemptWithStatusInMessage()
    {
        // 回归（2026-10-02 用户 404×3 实证）：404/403 等 4xx（除 408/429）是永久失败——
        // 同 URL 重试不会好转，重试 3 次只是把失败拖长三倍；必须单次终止并带状态码。
        // 408/429 与 5xx 保持瞬态重试语义（未映射 URL 由桩回 404）。
        var downloader = new HttpFileDownloader(new HttpClient(_handler), new HttpFileDownloaderOptions
        {
            MaxAttempts = 3,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        });

        var ex = await Assert.ThrowsAsync<DownloadException>(
            () => downloader.DownloadFileAsync(new DownloadRequest(
                "https://cdn.example.com/not-mapped.bin", _tempDir.FilePath("f.bin"), null, null)));

        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
        Assert.Single(_handler.Requests); // 永久失败不重试：单次请求即终止
    }
}
