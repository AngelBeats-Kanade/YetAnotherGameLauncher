namespace YetAnotherGameLauncher.Core.Models;

/// <summary>
/// 鸣潮资源包档位（2026-10-02 起作为启动参数、2026-10-19 起同时决定下载来源）：
/// 白名单与"生效档位"的单一事实源，配置校验 / 渠道 / 更新编排 / UI 共用。
/// </summary>
public static class ResourceTiers
{
    /// <summary>高清档（未设置时的生效档：官方默认 bundle = common + HD）。</summary>
    public const string Hd = "hd";

    /// <summary>标清档。</summary>
    public const string Sd = "sd";

    /// <summary>超高清档。</summary>
    public const string Uhd = "uhd";

    /// <summary>全部档位（顺序即设置页下拉顺序：默认档在前）。</summary>
    public static readonly IReadOnlyList<string> All = [Hd, Sd, Uhd];

    /// <summary>生效档位：未设置/空白 = <see cref="Hd"/>（旧行为：官方默认清单即 common + HD）。</summary>
    public static string Effective(string? tier) =>
        string.IsNullOrWhiteSpace(tier) ? Hd : tier.Trim();

    /// <summary>是否为白名单档位（原样小写匹配；大写/未知值拒收，与配置校验同口径）。</summary>
    public static bool IsValid(string? tier) => tier is not null && All.Contains(tier);

    /// <summary>指定档位之外的其余档位（显式清理的目标集合 + 保留清单的单一事实源）。</summary>
    public static IReadOnlyList<string> OtherTiers(string? tier)
    {
        var effective = Effective(tier);
        return [.. All.Where(t => !string.Equals(t, effective, StringComparison.Ordinal))];
    }
}
