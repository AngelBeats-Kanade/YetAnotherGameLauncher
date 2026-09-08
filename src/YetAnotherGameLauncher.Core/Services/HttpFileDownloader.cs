using System.Net;
using System.Net.Http.Headers;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// 基于 HttpClient 的文件下载器：.temp 临时文件 + HTTP Range 断点续传，
/// 瞬态网络错误线性退避重试，完成后按 size/MD5 校验再原子替换目标文件。
/// 与 wutheringwaves-cli-manager 的下载策略对齐。
/// </summary>
public sealed class HttpFileDownloader(
    HttpClient httpClient,
    HttpFileDownloaderOptions? options = null,
    ILogger? logger = null,
    SpeedLimiter? speedLimiter = null) : IDownloader
{
    private readonly HttpFileDownloaderOptions _options = options ?? new();

    /// <summary>全局限速器（多个下载共享同一预算）；null = 不限速。设置页可动态调整其 BytesPerSecond。</summary>
    public SpeedLimiter Limiter { get; } = speedLimiter ?? new();

    /// <summary>下载单个文件：.temp 断点续传 + 瞬态错误重试，完成后按 size/MD5 校验再原子落盘。</summary>
    public async Task DownloadFileAsync(
        DownloadRequest request,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destinationDirectory = Path.GetDirectoryName(request.DestinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var tempPath = request.DestinationPath + ".temp";
        DownloadException? lastError = null;

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            try
            {
                await DownloadAttemptAsync(request, tempPath, progress, cancellationToken).ConfigureAwait(false);
                Verify(request, tempPath);

                File.Move(tempPath, request.DestinationPath, overwrite: true);
                return;
            }
            catch (DownloadVerificationException ex)
            {
                // 内容不对：丢弃临时文件，从头重下
                FileUtilities.DeleteQuiet(tempPath);
                lastError = ex;
                logger?.LogWarning(ex, "Download verification failed (attempt {Attempt}/{Max}) ({Url})", attempt, _options.MaxAttempts, request.Url);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       && ex is not OperationCanceledException)
            {
                // 网络瞬态错误：保留 .temp 以便断点续传
                lastError = new DownloadException($"Download failed ({request.Url}): {ex.Message}", ex);
                logger?.LogWarning(ex, "Network error on download (attempt {Attempt}/{Max}) ({Url})", attempt, _options.MaxAttempts, request.Url);
            }

            if (attempt < _options.MaxAttempts)
            {
                await Task.Delay(_options.RetryBaseDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastError!;
    }

    private async Task DownloadAttemptAsync(
        DownloadRequest request,
        string tempPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var existingTempBytes = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (existingTempBytes > 0)
        {
            requestMessage.Headers.Range = new RangeHeaderValue(existingTempBytes, null);
        }

        using var response = await httpClient.SendAsync(
            requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // 仅当服务器确实按 Range 返回 206 时才算续传；返回 200 说明服务器忽略了 Range，需要重写
        var resume = response.StatusCode == HttpStatusCode.PartialContent && existingTempBytes > 0;
        if (!resume && File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        var startByte = resume ? existingTempBytes : 0;
        logger?.LogDebug("Downloading {Url}: resuming from byte {Start} (resume={Resume})", request.Url, startByte, resume);

        // CA2007 误报：await using 声明的 DisposeAsync 续体由编译器生成，无法对其追加 ConfigureAwait。
#pragma warning disable CA2007
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            tempPath,
            resume ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            _options.BufferSize,
            useAsync: true);
#pragma warning restore CA2007

        var written = startByte;
        progress?.Report(written);

        var buffer = new byte[_options.BufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            progress?.Report(written);

            var wait = Limiter.Acquire(read);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Verify(DownloadRequest request, string tempPath)
    {
        var actualLength = new FileInfo(tempPath).Length;
        if (request.ExpectedSize is long expectedSize && actualLength != expectedSize)
        {
            throw new DownloadVerificationException(
                $"Size mismatch for {request.DestinationPath}: expected {expectedSize} bytes, got {actualLength}.");
        }

        if (request.ExpectedMd5 is string expectedMd5)
        {
            var actualMd5 = Hashing.Md5Hex(tempPath);
            if (!string.Equals(actualMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw new DownloadVerificationException(
                    $"MD5 mismatch for {request.DestinationPath}: expected {expectedMd5}, got {actualMd5}.");
            }
        }
    }
}
