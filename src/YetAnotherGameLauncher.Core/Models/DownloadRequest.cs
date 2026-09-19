namespace YetAnotherGameLauncher.Core.Models;

/// <summary>
/// 单文件下载请求。期望尺寸 ≤ 0 或期望 MD5 为空表示上游未提供校验信息，
/// 下载完成后跳过对应校验项（而非按字面目标值比较）。
/// </summary>
public sealed record DownloadRequest(
    string Url,
    string DestinationPath,
    long? ExpectedSize,
    string? ExpectedMd5);
