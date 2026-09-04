namespace YetAnotherGameLauncher.Core.Models;

/// <summary>单文件下载请求。</summary>
public sealed record DownloadRequest(
    string Url,
    string DestinationPath,
    long? ExpectedSize,
    string? ExpectedMd5);
