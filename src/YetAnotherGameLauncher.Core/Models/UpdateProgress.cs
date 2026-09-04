namespace YetAnotherGameLauncher.Core.Models;

/// <summary>更新流程阶段。</summary>
public enum UpdatePhase
{
    Checking,
    Downloading,
    Patching,
    Verifying,
    CleaningUp,
    Done,
}

/// <summary>更新/下载进度快照，供 UI 绑定。</summary>
public sealed record UpdateProgress(
    UpdatePhase Phase,
    long TotalBytes,
    long DownloadedBytes,
    int FilesDone,
    int FilesTotal,
    string? CurrentItem);
