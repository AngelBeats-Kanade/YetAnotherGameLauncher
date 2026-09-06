namespace YetAnotherGameLauncher.Core.Services;

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

/// <summary>
/// Linux 兼容层工具：扫描 Steam 常见目录下的 Proton 版本，
/// 并把"启动方式 + Proton 版本"翻译成命令模板与环境变量。
/// </summary>
public static class CompatTools
{
    /// <summary>默认推荐的 Proton 版本。</summary>
    public const string DefaultProton = "dw-proton";

    public static IReadOnlyList<string> FindProtonVersions(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(home, ".steam", "steam", "compatibilitytools.d"),
            Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d"),
            Path.Combine(home, ".steam", "root", "steamapps", "common"),
        };

        var versions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
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
        var ordered = versions.OrderByDescending(v =>
            v.Equals(DefaultProton, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ordered;
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

    /// <summary>按优先级在已知目录中定位指定 Proton 版本；找不到返回 null。</summary>
    public static string? LocateProton(string protonVersion, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            Path.Combine(home, ".steam", "steam", "compatibilitytools.d"),
            Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d"),
            Path.Combine(home, ".steam", "root", "steamapps", "common"),
        ];
        return roots
            .Select(root => Path.Combine(root, protonVersion))
            .FirstOrDefault(Directory.Exists);
    }
}
