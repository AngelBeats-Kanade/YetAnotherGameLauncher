namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>可靠文件下载器：断点续传、失败重试、按 size/MD5 校验完整性。</summary>
public interface IDownloader
{
    /// <summary>
    /// 下载单个文件到目标路径。写入先落 <c>.temp</c> 临时文件，校验通过后原子替换。
    /// </summary>
    /// <param name="request">下载请求。</param>
    /// <param name="progress">可选进度回调，报告该文件已下载的绝对字节数（含续传部分）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DownloadFileAsync(
        Models.DownloadRequest request,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);
}
