namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>下载最终失败（网络错误重试耗尽等）。</summary>
public class DownloadException : Exception
{
    public DownloadException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>下载完成但内容校验（大小/MD5）失败。</summary>
public class DownloadVerificationException : DownloadException
{
    public DownloadVerificationException(string message) : base(message)
    {
    }
}
