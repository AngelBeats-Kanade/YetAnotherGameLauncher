using YetAnotherGameLauncher.Core.Dependencies;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Services;

/// <summary>目标不可用的原因（UI 据此给可操作文案）。</summary>
public enum WineTargetUnavailable
{
    /// <summary>直接运行模式：没有 Wine prefix，依赖不适用。</summary>
    DirectMode,

    /// <summary>需要 wine（系统或 Proton 内置）但一个都没有。</summary>
    WineMissing,

    /// <summary>umu/Proton 模式但本地没有可用 Proton（未准备兼容组件）。</summary>
    ProtonMissing,

    /// <summary>wine 可用但 prefix 未初始化（缺 drive_c，需先启动一次游戏）。</summary>
    PrefixMissing,
}

/// <summary>解析结果：Target 与 Reason 互斥（Target 非 null 即可用）。</summary>
/// <param name="Target">可用的安装目标；null = 不可用。</param>
/// <param name="Reason">不可用原因；Target 非 null 时为 null。</param>
public sealed record WineTargetResolution(WinePrefixTarget? Target, WineTargetUnavailable? Reason);

/// <summary>
/// 依赖安装目标解析（纯决策表，路径全部可注入）：
/// ① 直接运行 → 不适用；② 启动环境里"非生成本地"的自定义 WINEPREFIX → 系统 wine 挂该 prefix；
/// ③ 其余按 umu 布局：兼容数据根取环境 STEAM_COMPAT_DATA_PATH（手改配置的自定义 Proton prefix），
/// 缺省 = 统一 prefix 根 → Proton 定位（PROTONPATH 绝对目录 / compatibilitytools.d 精确名 /
/// 发行版前缀择新，flavor 表复用 provisioner）→ Proton 内置 wine（files/bin/wine）。
/// prefix 未初始化（缺 drive_c）不是异常而是可见状态：提示先启动一次游戏。
/// 适用边界（手改配置可达的遗留模式）：Proton 直启模板的环境不含 PROTONPATH，解析不到目录时按
/// ProtonMissing 提示；Wine 模式带绝对 PROTONPATH 时按 umu 布局解析（游戏本体走系统 wine 的话
/// 两边 prefix 不一致属配置自身矛盾）。
/// </summary>
public static class WinePrefixTargetResolver
{
    /// <summary>Proton 目录内 wine 可执行文件的相对路径。</summary>
    private static readonly string[] ProtonWineRelative = ["files", "bin", "wine"];

    /// <summary>解析依赖安装目标。</summary>
    /// <param name="mode">启动方式（设置页当前选择）。</param>
    /// <param name="gameId">游戏 id（定位统一 prefix）。</param>
    /// <param name="launchEnvironment">游戏启动环境变量（games.json launch.environment，可能含 WINEPREFIX/PROTONPATH/STEAM_COMPAT_DATA_PATH）。</param>
    /// <param name="protonRequest">Proton 请求（设置页所选发行版代号；null = 用环境里的 PROTONPATH 或默认发行版）。</param>
    /// <param name="systemWinePath">系统 wine 绝对路径（CompatTools.FindSystemWine 的注入结果；null = 未安装）。</param>
    /// <param name="dataHome">数据根（测试注入；缺省 AppPaths）。</param>
    public static WineTargetResolution Resolve(
        LaunchMode mode,
        string gameId,
        IReadOnlyDictionary<string, string>? launchEnvironment,
        string? protonRequest,
        string? systemWinePath,
        string? dataHome = null)
    {
        if (mode == LaunchMode.Direct)
        {
            return new WineTargetResolution(null, WineTargetUnavailable.DirectMode);
        }

        // 自定义 WINEPREFIX（用户手写模板指向 prefix 根之外的位置）：系统 wine + 该 prefix。
        // umu 推荐链生成的 WINEPREFIX（= 统一 prefix 根）不算自定义——它只是启动环境的一部分，
        // 真正的 wine prefix 是其 pfx 子目录
        var generatedPrefix = CompatTools.PrefixPathFor(gameId, dataHome);
        if (launchEnvironment?.TryGetValue("WINEPREFIX", out var customPrefix) == true
            && Path.IsPathRooted(customPrefix)
            && !string.Equals(Path.GetFullPath(customPrefix), generatedPrefix, StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(systemWinePath) || !FileUtilities.IsExecutableFile(systemWinePath))
            {
                return new WineTargetResolution(null, WineTargetUnavailable.WineMissing);
            }

            return PrefixTarget(systemWinePath, customPrefix, customPrefix);
        }

        // umu / Proton / wine 模式统一走 Proton 定位：PROTONPATH 绝对目录优先，
        // 其次 compatibilitytools.d 精确名，再按发行版前缀择最新（逐目录验 wine 可执行）。
        // 兼容数据根允许手改配置指定自定义 STEAM_COMPAT_DATA_PATH（生成模板恒等于统一 prefix 根，
        // 取值相同无行为差）；wine prefix 与状态目录都在该根下（pfx/ 与 .yagl-deps/）
        var compatDataRoot = launchEnvironment?.TryGetValue("STEAM_COMPAT_DATA_PATH", out var compatPath) == true
            && Path.IsPathRooted(compatPath)
                ? compatPath
                : generatedPrefix;
        var protonDir = ResolveProtonDirectory(launchEnvironment, protonRequest, dataHome);
        if (protonDir is not null)
        {
            var protonWine = Path.Combine([protonDir, .. ProtonWineRelative]);
            if (!FileUtilities.IsExecutableFile(protonWine))
            {
                return new WineTargetResolution(null, WineTargetUnavailable.ProtonMissing);
            }

            return PrefixTarget(protonWine, Path.Combine(compatDataRoot, "pfx"), compatDataRoot);
        }

        if (mode == LaunchMode.Wine)
        {
            if (string.IsNullOrEmpty(systemWinePath) || !FileUtilities.IsExecutableFile(systemWinePath))
            {
                return new WineTargetResolution(null, WineTargetUnavailable.WineMissing);
            }

            return PrefixTarget(systemWinePath, compatDataRoot, compatDataRoot);
        }

        return new WineTargetResolution(null, WineTargetUnavailable.ProtonMissing);
    }

    /// <summary>组装目标；prefix 未初始化（缺 drive_c）按可见状态返回而非异常。</summary>
    private static WineTargetResolution PrefixTarget(
        string wineExecutable, string winePrefixDirectory, string stateRoot)
    {
        if (!Directory.Exists(Path.Combine(winePrefixDirectory, "drive_c")))
        {
            return new WineTargetResolution(null, WineTargetUnavailable.PrefixMissing);
        }

        return new WineTargetResolution(
            new WinePrefixTarget(
                wineExecutable,
                winePrefixDirectory,
                Path.Combine(winePrefixDirectory, "drive_c", "windows", "Fonts"),
                Path.Combine(stateRoot, DependencyPaths.StateDirName)),
            null);
    }

    /// <summary>定位本地 Proton 目录：环境 PROTONPATH 绝对目录 → compatibilitytools.d 精确名 →
    /// 发行版前缀择最新（数字段自然序，单一事实源 CompatTools.NumericSortKey）。
    /// 两条清单路径共用同一就绪判定（内置 wine 可执行）：精确名目录不就绪时与择新分支一致地
    /// 回退到发行版扫描，最终无可用目录才报 ProtonMissing。
    /// 择新逐目录验证 wine 可执行：半解包的更新中目录/缺 wine 的残目录被跳过、回退旧可用版
    /// （对齐 provisioner 的逐目录就绪过滤语义；错误架构目录仍视为可用——其 wine 存在，
    /// 架构失配由导入执行时暴露，与 provisioner 的 ELF 头过滤存在已知差异）。</summary>
    private static string? ResolveProtonDirectory(
        IReadOnlyDictionary<string, string>? launchEnvironment,
        string? protonRequest,
        string? dataHome)
    {
        if (launchEnvironment?.TryGetValue("PROTONPATH", out var envPath) == true
            && Path.IsPathRooted(envPath)
            && Directory.Exists(envPath))
        {
            return envPath;
        }

        var request = string.IsNullOrWhiteSpace(protonRequest) ? null : protonRequest.Trim();
        var compatRoot = UmuPaths.SteamCompatRoot(dataHome);
        if (request is not null)
        {
            var byName = Path.Combine(compatRoot, request);
            if (IsUsableProton(byName))
            {
                return byName;
            }
        }

        // 代号 → 本地目录名前缀（flavor 表单一事实源在 provisioner；未知代号不扫描）
        var localPrefix = request is null ? null : UmuComponentProvisioner.MatchFlavorLocalPrefix(request);
        if (localPrefix is null || !Directory.Exists(compatRoot))
        {
            return null;
        }

        return Directory.EnumerateDirectories(compatRoot)
            .Where(d => Path.GetFileName(d).StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(IsUsableProton)
            .OrderByDescending(d => CompatTools.NumericSortKey(Path.GetFileName(d)), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>目录是否带可执行的内置 wine（依赖安装唯一关心的就绪条件）。</summary>
    private static bool IsUsableProton(string protonDirectory) =>
        FileUtilities.IsExecutableFile(Path.Combine([protonDirectory, .. ProtonWineRelative]));
}
