using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>根据本地版本与渠道提供的差分入口，决定全量还是增量更新。</summary>
public static class UpdatePlanner
{
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
    /// <summary>remoteVersion 是否比 localVersion 新。本地未安装（null/空）时返回 false（谈不上"更新"）。</summary>
    public static bool IsNewer(string remoteVersion, string? localVersion)
    {
        if (string.IsNullOrEmpty(localVersion))
        {
            return false;
        }

        if (Version.TryParse(remoteVersion, out var remote)
            && Version.TryParse(localVersion, out var local))
        {
            return remote > local;
        }

        return !string.Equals(remoteVersion, localVersion, StringComparison.Ordinal);
    }

    public static bool IsSame(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
