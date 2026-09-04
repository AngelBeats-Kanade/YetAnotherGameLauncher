namespace YetAnotherGameLauncher.Core.Models;

/// <summary>渠道版本信息：最新版本、可用的差分入口、预下载窗口状态。</summary>
public sealed class ChannelVersionInfo
{
    /// <summary>服务器当前最新版本。</summary>
    public string LatestVersion { get; init; } = "";

    /// <summary>可对最新版本做增量差分的源版本列表（"从该版本 → 最新版本"）。</summary>
    public IReadOnlyList<string> PatchSourceVersions { get; init; } = [];

    /// <summary>预下载窗口是否开启（鸣潮 predownloadSwitch 且存在 predownload.config）。</summary>
    public bool PredownloadAvailable { get; init; }

    /// <summary>预下载指向的目标版本。</summary>
    public string? PredownloadVersion { get; init; }

    /// <summary>可对预下载版本做增量差分的源版本列表。</summary>
    public IReadOnlyList<string> PredownloadPatchSourceVersions { get; init; } = [];
}
