using System.Net;
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
    ILogger? logger = null) : IDownloader
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly HttpFileDownloaderOptions _options = options ?? new();

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
                await DownloadAttemptAsync(request, tempPath, progress, cancellationToken);
                Verify(request, tempPath);

                File.Move(tempPath, request.DestinationPath, overwrite: true);
                return;
            }
            catch (DownloadVerificationException ex)
            {
                // 内容不对：丢弃临时文件，从头重下
                DeleteQuiet(tempPath);
                lastError = ex;
                logger?.LogWarning(ex, "第 {Attempt}/{Max} 次下载校验失败（{Url}）", attempt, _options.MaxAttempts, request.Url);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       && ex is not OperationCanceledException)
            {
                // 网络瞬态错误：保留 .temp 以便断点续传
                lastError = new DownloadException($"下载失败（{request.Url}）：{ex.Message}", ex);
                logger?.LogWarning(ex, "第 {Attempt}/{Max} 次下载网络错误（{Url}）", attempt, _options.MaxAttempts, request.Url);
            }

            if (attempt < _options.MaxAttempts)
            {
                await Task.Delay(_options.RetryBaseDelay * attempt, cancellationToken);
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
            requestMessage.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingTempBytes, null);
        }

        using var response = await _httpClient.SendAsync(
            requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // 仅当服务器确实按 Range 返回 206 时才算续传；返回 200 说明服务器忽略了 Range，需要重写
        var resume = response.StatusCode == HttpStatusCode.PartialContent && existingTempBytes > 0;
        if (!resume && File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        var startByte = resume ? existingTempBytes : 0;
        logger?.LogDebug("下载 {Url}：从 {Start} 字节开始（resume={Resume}）", request.Url, startByte, resume);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            tempPath,
            resume ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            _options.BufferSize,
            useAsync: true);

        var written = startByte;
        progress?.Report(written);

        var buffer = new byte[_options.BufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
            progress?.Report(written);
        }

        await target.FlushAsync(cancellationToken);
    }

    private static void Verify(DownloadRequest request, string tempPath)
    {
        var actualLength = new FileInfo(tempPath).Length;
        if (request.ExpectedSize is long expectedSize && actualLength != expectedSize)
        {
            throw new DownloadVerificationException(
                $"文件大小校验失败（{request.DestinationPath}）：期望 {expectedSize} 字节，实际 {actualLength} 字节。");
        }

        if (request.ExpectedMd5 is string expectedMd5)
        {
            var actualMd5 = Hashing.Md5Hex(tempPath);
            if (!string.Equals(actualMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw new DownloadVerificationException(
                    $"MD5 校验失败（{request.DestinationPath}）：期望 {expectedMd5}，实际 {actualMd5}。");
            }
        }
    }

    private static void DeleteQuiet(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 尽力清理，失败不影响主流程
        }
    }
}
