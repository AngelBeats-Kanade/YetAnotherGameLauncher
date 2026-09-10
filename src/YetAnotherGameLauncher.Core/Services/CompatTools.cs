namespace YetAnotherGameLauncher.Core.Services;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>Linux 下的启动方式（与 UI 的选择器一一对应）。</summary>
public enum LaunchMode
{
    /// <summary>直接运行（官方默认方式，Windows 上唯一方式）。</summary>
    Direct,

    /// <summary>通过 Wine 启动。</summary>
    Wine,

    /// <summary>通过 Proton（含 dw-proton 等自定义版本）启动。</summary>
    Proton,

    /// <summary>自定义命令模板（不自动生成）。</summary>
    Custom,
}

/// <summary>一次推荐生成的完整启动配置：Proton 版本名 + 命令模板 + 合并后的环境变量。</summary>
/// <param name="ProtonVersion">被选中的 Proton 版本名。</param>
/// <param name="CommandTemplate">命令模板（proton 脚本路径 + run {exe}）。</param>
/// <param name="Environment">STEAM_COMPAT_* 与游戏推荐环境变量（含 NVIDIA 分支）的合并结果。</param>
public sealed record CompatLaunch(string ProtonVersion, string CommandTemplate, Dictionary<string, string> Environment);

/// <summary>
/// Linux 兼容层工具：扫描 Steam 常见目录下的 Proton 版本，
/// 并把"启动方式 + Proton 版本"翻译成命令模板与环境变量。
/// </summary>
public static class CompatTools
{
    /// <summary>默认推荐的 Proton 版本。</summary>
    public const string DefaultProton = "dw-proton";

    /// <summary>Steam 常见安装根目录（库目录的父级；Proton 版本扫描与 compatdata prefix 探测共用）。</summary>
    internal static string[] SteamRoots(string home) =>
    [
        Path.Combine(home, ".steam", "steam"),
        Path.Combine(home, ".local", "share", "Steam"),
        Path.Combine(home, ".steam", "root"),
    ];

    /// <summary>Steam 兼容工具与自带运行时的常见根目录（供版本扫描与定位共用）。</summary>
    private static string[] ProtonRoots(string home) =>
    [
        Path.Combine(home, ".steam", "steam", "compatibilitytools.d"),
        Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d"),
        Path.Combine(home, ".steam", "root", "steamapps", "common"),
    ];

    /// <summary>扫描已知目录中的可用 Proton 版本名（默认推荐版本置顶，字典序）。</summary>
    public static IReadOnlyList<string> FindProtonVersions(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var versions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ProtonRoots(home))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                // 自定义工具目录取目录名；Steam 自带运行时仅取 Proton* 目录
                if (root.EndsWith("common", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                versions.Add(name);
            }
        }

        // 默认推荐版本置顶
        return versions
            .OrderByDescending(v => v.Equals(DefaultProton, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 生成 Proton 启动的命令模板与环境变量。
    /// 环境变量值支持 {installDir} 占位符（与 LaunchOptions 的既有展开一致）。
    /// </summary>
    public static (string CommandTemplate, Dictionary<string, string> Environment) BuildProtonLaunch(
        string protonVersion, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var protonDir = LocateProton(protonVersion, home)
                        ?? Path.Combine(home, ".steam", "steam", "compatibilitytools.d", protonVersion);

        var command = $"\"{Path.Combine(protonDir, "proton")}\" run {{exe}}";
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["STEAM_COMPAT_DATA_PATH"] = "{installDir}/compatdata",
            ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = Path.Combine(home, ".steam", "steam"),
        };
        return (command, environment);
    }

    /// <summary>
    /// 挑选推荐 Proton 版本：GE-Proton 取数字最新（终末地需 10-31+，鸣潮需 10-9+，
    /// 按数字段自然比较——字典序会把 10-9 排到 10-31 之后），其次内置默认 dw-proton，
    /// 再次任意第一个；无可用版本返回 null。
    /// </summary>
    public static string? PickRecommendedProton(IReadOnlyList<string> versions)
    {
        if (versions.Count == 0)
        {
            return null;
        }

        return versions
            .OrderByDescending(v => v.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase) ? 2
                : v.Equals(DefaultProton, StringComparison.OrdinalIgnoreCase) ? 1
                : v.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ? 0
                : -1)
            .ThenByDescending(NumericSortKey, StringComparer.Ordinal)
            .First();
    }

    /// <summary>自然排序键：把字符串里的数字段左侧补零成定长（"GE-Proton10-31" → …000010000031），
    /// 使数字比较正确（"10-31" &gt; "10-9"）。</summary>
    internal static string NumericSortKey(string version) =>
        string.Concat(Regex.Matches(version, @"\d+").Select(m => m.Value.PadLeft(6, '0')));

    /// <summary>
    /// 按游戏给出的社区推荐环境变量（ACE 反作弊最佳实践）：
    /// 鸣潮需伪装 SteamOS 才能过反作弊，NVIDIA 显卡补 DXVK-NVAPI（探测结果由平台注入）；终末地无必填项。
    /// </summary>
    public static Dictionary<string, string> RecommendedEnvironment(string gameId, bool nvidiaGpuPresent = false)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (gameId.Contains("wuthering", StringComparison.OrdinalIgnoreCase))
        {
            environment["SteamOS"] = "1";
            if (nvidiaGpuPresent)
            {
                environment["PROTON_ENABLE_NVAPI"] = "1";
            }
        }

        return environment;
    }

    /// <summary>
    /// 一站式生成社区推荐的启动配置（首运落盘与启动设置卡共用，单一事实源）：
    /// 按 <see cref="PickRecommendedProton"/> 挑版本，命令模板与环境变量取
    /// <see cref="BuildProtonLaunch"/> 与 <see cref="RecommendedEnvironment"/> 的合并；
    /// 无可用 Proton 版本返回 null（调用方自行回退，如 wine）。
    /// </summary>
    public static CompatLaunch? BuildRecommendedLaunch(
        string gameId, IReadOnlyList<string> protonVersions, bool nvidiaGpuPresent = false, string? home = null)
    {
        if (PickRecommendedProton(protonVersions) is not { } version)
        {
            return null;
        }

        var (commandTemplate, environment) = BuildProtonLaunch(version, home);
        foreach (var (key, value) in RecommendedEnvironment(gameId, nvidiaGpuPresent))
        {
            environment[key] = value;
        }

        return new CompatLaunch(version, commandTemplate, environment);
    }

    /// <summary>按优先级在已知目录中定位指定 Proton 版本；找不到返回 null。</summary>
    public static string? LocateProton(string protonVersion, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ProtonRoots(home)
            .Select(root => Path.Combine(root, protonVersion))
            .FirstOrDefault(Directory.Exists);
    }
}
