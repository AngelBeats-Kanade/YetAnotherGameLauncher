using System.Net;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.Core.Utilities;
using Xunit;

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
        var reports = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var progress = new Progress<long>(reports.Enqueue);

        await CreateDownloader().DownloadFileAsync(Request(), progress, Ct);

        Assert.Equal(Content.Length, reports.Last());
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
