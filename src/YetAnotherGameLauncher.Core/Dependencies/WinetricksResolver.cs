using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Dependencies;

/// <summary>winetricks 解析结果。</summary>
/// <param name="ExecutablePath">winetricks 可执行文件绝对路径。</param>
/// <param name="Bundled">true = Proton 自带（<c>protonfixes/files/bin/winetricks</c>）；false = PATH 上的系统 winetricks。</param>
public sealed record WinetricksResolution(string ExecutablePath, bool Bundled);

/// <summary>
/// winetricks 定位（纯决策，路径可注入）：优先 Proton 自带的 <c>protonfixes/files/bin/winetricks</c>
/// （dwproton 实测存在），否则回退 PATH 上的系统 winetricks；都没有返回 null 由调用方报
/// <see cref="DependencyFailureKind.WinetricksMissing"/>。
/// </summary>
public static class WinetricksResolver
{
    /// <summary>Proton 自带 winetricks 的相对路径段。</summary>
    private static readonly string[] BundledRelative = ["protonfixes", "files", "bin", "winetricks"];

    /// <summary>解析 winetricks 路径；protonRoot 为 null（系统 wine / 自定义 prefix）时跳过自带候选。</summary>
    /// <param name="protonRoot">Proton 根目录（含 files/bin/wine 的上级），null = 非 Proton 布局。</param>
    /// <param name="pathLookup">PATH 查找注入（默认 CompatTools.FindOnPath；测试注入固定结果）。</param>
    public static WinetricksResolution? Resolve(
        string? protonRoot, Func<string, string?>? pathLookup = null)
    {
        if (protonRoot is not null)
        {
            var bundled = Path.Combine([protonRoot, .. BundledRelative]);
            if (FileUtilities.IsExecutableFile(bundled))
            {
                return new WinetricksResolution(bundled, Bundled: true);
            }
        }

        var path = (pathLookup ?? (name => CompatTools.FindOnPath(name)))("winetricks");
        return path is null ? null : new WinetricksResolution(path, Bundled: false);
    }
}
