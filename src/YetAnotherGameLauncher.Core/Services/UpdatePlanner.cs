using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>根据本地版本与渠道提供的差分入口，决定全量还是增量更新。</summary>
public static class UpdatePlanner
{
    /// <summary>决定更新策略：本地版本命中差分入口走增量，否则全量同步。</summary>
    /// <param name="localVersion">本地已安装版本；null/空 表示尚未安装。</param>
    /// <param name="targetVersion">目标版本。</param>
    /// <param name="availablePatchSourceVersions">
    /// 渠道提供的差分入口版本列表（"从该版本 → 目标版本"的差分包可用）。
    /// 与鸣潮 patchConfig 一致，按字符串精确匹配。
    /// </param>
    public static UpdatePlan Plan(
        string? localVersion,
        string targetVersion,
        IReadOnlyCollection<string> availablePatchSourceVersions)
    {
        var canIncremental = !string.IsNullOrEmpty(localVersion)
                             && availablePatchSourceVersions.Contains(localVersion, StringComparer.Ordinal);

        return new UpdatePlan(
            canIncremental ? UpdateStrategy.Incremental : UpdateStrategy.FullSync,
            localVersion ?? "",
            targetVersion);
    }
}

/// <summary>版本号比较。数字版本（可被 System.Version 解析）按段比较，否则退化为字符串比较。</summary>
public static class VersionComparison
{
    /// <summary>
    /// remoteVersion 是否比 localVersion 新。本地未安装（null/空）返回 false（谈不上"更新"）；
    /// 远端版本缺失（null/空白）同样返回 false——空远端通常来自渠道响应异常，
    /// 按"字符串不等"退化会误报有更新且更新目标为空（2026-09-20 复审修复）。
    /// </summary>
    public static bool IsNewer(string remoteVersion, string? localVersion)
    {
        if (string.IsNullOrWhiteSpace(remoteVersion) || string.IsNullOrEmpty(localVersion))
        {
            return false;
        }

        if (Version.TryParse(remoteVersion, out var remote)
            && Version.TryParse(localVersion, out var local))
        {
            // System.Version 对缺失段按"更旧"参与比较（文档化行为："an unknown component is
            // assumed to be older"）——渠道版本串段数漂移（3.6 → 3.6.0）时同版本被误判"有更新"，
            // 正向误报重跑一次同内容更新（F15）。缺失段按 0 归一后再比，"3.6" ≡ "3.6.0"
            return NormalizeMissingSegments(remote) > NormalizeMissingSegments(local);
        }

        return !string.Equals(remoteVersion, localVersion, StringComparison.Ordinal);

        static Version NormalizeMissingSegments(Version version) =>
            version.Build < 0 || version.Revision < 0
                ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0))
                : version;
    }
}
