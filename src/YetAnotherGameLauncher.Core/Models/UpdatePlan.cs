namespace YetAnotherGameLauncher.Core.Models;

/// <summary>更新策略：全量同步或增量差分。</summary>
public enum UpdateStrategy
{
    /// <summary>按清单逐文件比对并补齐（首次安装/无可用差分时）。</summary>
    FullSync,

    /// <summary>下载差分包并用补丁器合成新版本。</summary>
    Incremental,
}

/// <summary>一次更新动作的计划。</summary>
public sealed record UpdatePlan(
    UpdateStrategy Strategy,
    string FromVersion,
    string ToVersion);
