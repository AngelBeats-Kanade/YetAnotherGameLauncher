namespace YetAnotherGameLauncher.Core.Services;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>Linux 下的启动方式（与 UI 的选择器一一对应）。</summary>
public enum LaunchMode
{
    /// <summary>直接运行（官方默认方式，Windows 上唯一方式）。</summary>
    Direct,

    /// <summary>通过 umu-launcher 启动（自动管理 Steam Runtime 容器与 Proton，社区最稳路线）。</summary>
    Umu,

    /// <summary>通过 Wine 启动。</summary>
    Wine,

    /// <summary>通过 Proton（含 dw-proton 等自定义版本）启动。</summary>
    Proton,

    /// <summary>自定义命令模板（不自动生成）。</summary>
    Custom,
}

/// <summary>
/// 一次推荐生成的完整启动配置：启动方式 + 运行时名 + 命令模板 + 合并后的环境变量。
/// RuntimeName 仅 Proton 模式有意义（所选版本名）；umu 模式已安装时为 "umu"，未安装（引导安装前置模板）为 null。
/// </summary>
/// <param name="Mode">启动方式。</param>
/// <param name="RuntimeName">运行时名（Proton 版本名 / umu）；无运行时为 null。</param>
/// <param name="CommandTemplate">命令模板（运行时可执行文件 + {exe}）。</param>
/// <param name="Environment">prefix 定位与游戏推荐环境变量（含 NVIDIA 分支）的合并结果。</param>
public sealed record CompatLaunch(
    LaunchMode Mode,
    string? RuntimeName,
    string CommandTemplate,
    Dictionary<string, string> Environment);

/// <summary>
/// Linux 兼容层工具：发现 Wine 运行时（umu-launcher / 系统 wine / Lutris runner）与 Steam 下的
/// Proton 版本，并把"启动方式 + 运行时"翻译成命令模板与环境变量。
/// Wine prefix 统一放在应用数据目录（{dataHome}/yagl/prefixes/&lt;gameId&gt;），绝不写进游戏安装目录——
/// 安装同步的清单外清理会删除安装目录内的一切，混放会被"校验修复"整个毁掉。
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

    /// <summary>umu-run 的已知固定安装位置（先找应用引导安装目录，再找官方默认位置）。</summary>
    private static string[] UmuFixedLocations(string home) =>
    [
        Path.Combine(home, ".local", "share", "yagl", "umu", "umu-run"),
        Path.Combine(home, ".local", "bin", "umu-run"),
        Path.Combine(home, ".local", "share", "umu", "umu-run"),
    ];

    /// <summary>
    /// 扫描已知目录中的可用 Proton 版本名（默认推荐版本置顶，字典序）。
    /// </summary>
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
    /// 定位 umu-launcher 的 umu-run 可执行文件：应用引导安装目录 → 官方默认位置 → PATH 逐目录。
    /// Linux 上要求可执行位（没有执行位等于不可用）；找不到返回 null。
    /// </summary>
    /// <param name="pathValue">PATH 环境变量的值；null = 读真实环境（测试注入空串禁用 PATH 扫描）。</param>
    /// <param name="home">用户主目录；null = 取当前用户。</param>
    public static string? FindUmuRun(string? pathValue = null, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        pathValue ??= Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var candidate in UmuFixedLocations(home))
        {
            if (IsExecutableFile(candidate))
            {
                return candidate;
            }
        }

        return SearchPath(pathValue, "umu-run");
    }

    /// <summary>
    /// 定位系统 wine：按 PATH 逐目录找 "wine"（发行版 wine 一定在 PATH 里）。
    /// Linux 上要求可执行位；找不到返回 null。
    /// </summary>
    /// <param name="pathValue">PATH 环境变量的值；null = 读真实环境（测试注入空串禁用 PATH 扫描）。</param>
    /// <param name="home">用户主目录（保留参数，与其它发现函数签名一致）。</param>
    public static string? FindSystemWine(string? pathValue = null, string? home = null) =>
        SearchPath(
            pathValue ?? Environment.GetEnvironmentVariable("PATH") ?? "",
            "wine");

    /// <summary>列出 Lutris 的 Wine runner 版本名（~/.local/share/lutris/runners/wine/ 下含 bin/ 的目录）。</summary>
    public static IReadOnlyList<string> FindLutrisWineVersions(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".local", "share", "lutris", "runners", "wine");
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(root)
            .Where(dir => Directory.Exists(Path.Combine(dir, "bin")))
            .Select(dir => Path.GetFileName(dir))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>定位 Lutris Wine runner 的 wine 可执行文件；找不到返回 null。</summary>
    public static string? LocateLutrisWine(string version, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var wine = Path.Combine(
            home, ".local", "share", "lutris", "runners", "wine", version, "bin", "wine");
        return IsExecutableFile(wine) ? wine : null;
    }

    /// <summary>Wine prefix 统一根目录：{dataHome}/yagl/prefixes（dataHome 缺省 ~/.local/share）。</summary>
    public static string PrefixRoot(string? home = null, string? dataHome = null) =>
        Path.Combine(dataHome ?? DefaultDataHome(home), "yagl", "prefixes");

    /// <summary>指定游戏的 Wine prefix 路径：{dataHome}/yagl/prefixes/&lt;gameId&gt;。</summary>
    public static string PrefixPathFor(string gameId, string? home = null, string? dataHome = null) =>
        Path.Combine(PrefixRoot(home, dataHome), gameId);

    /// <summary>
    /// 生成 umu-launcher 启动配置：`{umu-run路径} {exe}` + GAMEID/UMU_ID/WINEPREFIX。
    /// GAMEID 用 umu-&lt;gameId&gt;：能命中 umu 数据库时自动套用社区修复，未命中则走默认行为；
    /// 两个变量同时设置（umu 1.1 起改用 UMU_ID，旧版本只认 GAMEID）。
    /// umuRunPath 为 null 表示尚未安装（生成裸 umu-run 模板供引导安装就位后直接使用）。
    /// </summary>
    public static CompatLaunch BuildUmuLaunch(
        string gameId, string? umuRunPath, string? home = null, string? dataHome = null)
    {
        var umuId = $"umu-{gameId}";
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GAMEID"] = umuId,
            ["UMU_ID"] = umuId,
            ["WINEPREFIX"] = PrefixPathFor(gameId, home, dataHome),
        };
        return new CompatLaunch(
            LaunchMode.Umu,
            umuRunPath is null ? null : "umu",
            $"{QuoteIfNeeded(umuRunPath ?? "umu-run")} {{exe}}",
            environment);
    }

    /// <summary>生成系统 Wine 启动配置：`wine {exe}` + WINEPREFIX 指向统一 prefix 位置。
    /// winePath 为 null 表示尚未安装（生成裸 wine 模板，路径由 PATH 解析）。</summary>
    public static CompatLaunch BuildWineLaunch(
        string gameId, string? winePath, string? home = null, string? dataHome = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WINEPREFIX"] = PrefixPathFor(gameId, home, dataHome),
        };
        return new CompatLaunch(
            LaunchMode.Wine,
            winePath is null ? null : "wine",
            $"{QuoteIfNeeded(winePath ?? "wine")} {{exe}}",
            environment);
    }

    /// <summary>
    /// 生成 Proton 启动的命令模板与环境变量。
    /// 环境变量值支持 {installDir} 占位符（与 LaunchOptions 的既有展开一致）；
    /// prefix（STEAM_COMPAT_DATA_PATH）落在统一数据目录，不再写进安装目录。
    /// </summary>
    public static CompatLaunch BuildProtonLaunch(
        string gameId, string protonVersion, string? home = null, string? dataHome = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var protonDir = LocateProton(protonVersion, home)
                        ?? Path.Combine(home, ".steam", "steam", "compatibilitytools.d", protonVersion);

        // 生成的 proton 脚本永远是绝对路径（版本名可能带空格，如 "Proton Hotfix"），无条件加引号
        var command = $"\"{Path.Combine(protonDir, "proton")}\" run {{exe}}";
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["STEAM_COMPAT_DATA_PATH"] = PrefixPathFor(gameId, home, dataHome),
            ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = Path.Combine(home, ".steam", "steam"),
        };
        return new CompatLaunch(LaunchMode.Proton, protonVersion, command, environment);
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
    /// 一站式生成社区推荐的启动配置（首运落盘与启动设置卡共用，单一事实源）。
    /// 推荐链：umu-launcher（社区最稳，自动管理 Steam Runtime 与 Proton）→ Proton 直启 → 系统 wine。
    /// 什么都装了也没有时仍返回 umu 模板（RuntimeName=null）：先给引导安装铺路，装好即可启动。
    /// umu/wine 路径由调用方发现后注入（生产走 <see cref="FindUmuRun"/>/<see cref="FindSystemWine"/>，
    /// 测试显式传值保证确定性；空字符串归一为"未发现"，方便测试禁用真机 PATH 扫描）。
    /// </summary>
    public static CompatLaunch BuildRecommendedLaunch(
        string gameId,
        IReadOnlyList<string> protonVersions,
        bool nvidiaGpuPresent = false,
        string? home = null,
        string? dataHome = null,
        string? umuRunPath = null,
        string? winePath = null)
    {
        // 空串 = 测试显式声明"没装"，与 null = 现场发现区分
        umuRunPath = string.IsNullOrEmpty(umuRunPath) ? null : umuRunPath;
        winePath = string.IsNullOrEmpty(winePath) ? null : winePath;

        CompatLaunch launch;
        if (umuRunPath is not null)
        {
            launch = BuildUmuLaunch(gameId, umuRunPath, home, dataHome);
        }
        else if (PickRecommendedProton(protonVersions) is { } version)
        {
            launch = BuildProtonLaunch(gameId, version, home, dataHome);
        }
        else if (winePath is not null)
        {
            launch = BuildWineLaunch(gameId, winePath, home, dataHome);
        }
        else
        {
            // 未发现任何运行时：裸 umu 模板（引导安装完成后即可启动，umu 首启自动下载 GE-Proton）
            launch = BuildUmuLaunch(gameId, null, home, dataHome);
        }

        foreach (var (key, value) in RecommendedEnvironment(gameId, nvidiaGpuPresent))
        {
            launch.Environment[key] = value;
        }

        return launch;
    }

    /// <summary>判断环境变量键是否由推荐生成（切启动方式时应清除）：STEAM_COMPAT_*、umu 系列、WINEPREFIX、PROTONPATH 与游戏推荐项。</summary>
    public static bool IsGeneratedEnvironmentKey(string key) =>
        key.StartsWith("STEAM_COMPAT_", StringComparison.OrdinalIgnoreCase)
        || key.Equals("GAMEID", StringComparison.OrdinalIgnoreCase)
        || key.Equals("UMU_ID", StringComparison.OrdinalIgnoreCase)
        || key.Equals("WINEPREFIX", StringComparison.OrdinalIgnoreCase)
        || key.Equals("PROTONPATH", StringComparison.OrdinalIgnoreCase)
        || key.Equals("SteamOS", StringComparison.Ordinal)
        || key.Equals("PROTON_ENABLE_NVAPI", StringComparison.Ordinal);

    /// <summary>按优先级在已知目录中定位指定 Proton 版本；找不到返回 null。</summary>
    public static string? LocateProton(string protonVersion, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ProtonRoots(home)
            .Select(root => Path.Combine(root, protonVersion))
            .FirstOrDefault(Directory.Exists);
    }

    /// <summary>按 PATH 逐目录查找可执行文件；找不到返回 null（启动预检与运行时发现共用）。</summary>
    /// <param name="name">要查找的命令名。</param>
    /// <param name="pathValue">PATH 环境变量的值；null = 读真实环境（测试注入空串禁用 PATH 扫描）。</param>
    public static string? FindOnPath(string name, string? pathValue = null) =>
        SearchPath(pathValue ?? Environment.GetEnvironmentVariable("PATH") ?? "", name);

    /// <summary>按 PATH 逐目录查找名为 <paramref name="name"/> 的可执行文件；找不到返回 null。
    /// 分隔符必须用 <see cref="Path.PathSeparator"/>：Windows 是 ';'，硬编码 ':' 会把盘符 'C:' 切开。</summary>
    private static string? SearchPath(string pathValue, string name)
    {
        foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (IsExecutableFile(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>文件是否存在且可执行：Linux 校验 UserExecute 位；Windows 无执行位概念，存在即可。</summary>
    private static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>路径含空格时加引号（模板按空格切分，含空格的运行时路径不加引号会被截断）。</summary>
    private static string QuoteIfNeeded(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;

    /// <summary>数据目录缺省值：~/.local/share（XDG_DATA_HOME 由组合根经 AppPaths 解析后注入）。</summary>
    private static string DefaultDataHome(string? home)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share");
    }
}
