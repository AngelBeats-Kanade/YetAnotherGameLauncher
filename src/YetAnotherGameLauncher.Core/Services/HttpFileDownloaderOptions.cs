namespace YetAnotherGameLauncher.Core.Services;

/// <summary>HttpFileDownloader 的行为参数（测试可注入以加速）。</summary>
public sealed class HttpFileDownloaderOptions
{
    /// <summary>含首次在内的最大尝试次数。</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>第 n 次重试前的退避 = RetryBaseDelay * n。</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>流式拷贝缓冲区大小（字节）。</summary>
    public int BufferSize { get; set; } = 64 * 1024;
}
