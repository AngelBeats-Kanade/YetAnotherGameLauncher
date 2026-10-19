using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>
/// 资源包档位的本地化显示名（设置页下拉 / 状态行 / 清理与补包文案共用，
/// 避免同一"档位 → 文案键"映射在多处各写一份而漂移）。
/// </summary>
internal static class ResourceTierText
{
    /// <summary>档位显示名；白名单外的值回退 hd 文案（配置校验层已拒，这里只是兜底不抛）。</summary>
    public static string DisplayName(ILocalizationService loc, string tier) => loc[tier switch
    {
        ResourceTiers.Sd => "launch_resourceQualitySd",
        ResourceTiers.Uhd => "launch_resourceQualityUhd",
        _ => "launch_resourceQualityHd",
    }];
}
