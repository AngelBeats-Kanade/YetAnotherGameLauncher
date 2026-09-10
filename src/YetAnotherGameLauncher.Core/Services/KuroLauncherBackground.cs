using System.Text.Json;
using System.Text.RegularExpressions;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// 库洛官方启动器（KRLauncher，WebView2 壳）背景数据探测：
/// ① 启动器网页端拉取的运营配置（switch.json，含背景视频/首帧图直链）会被 Chromium 缓存在
///    KRLauncher 数据目录的 Cache_Data 下，扫描提取即可复用官方同款背景。缓存根候选按平台取：
///    Windows 为 %APPDATA%\KRLauncher；Linux 上官启是 Windows 程序，其 %APPDATA% 落在
///    Wine/Proton prefix 内（~/.wine、$WINEPREFIX、Steam compatdata），逐 prefix 探测；
/// ② 游戏目录旁的 kr_game_cache/animate_bg/&lt;hash&gt;/home_N.jpg 帧序列（N 为帧序号）作为兜底。
/// </summary>
public static partial class KuroLauncherBackground
{
    /// <summary>游戏安装目录向上探测 kr_game_cache 的最大层数（…\Wuthering Waves\Wuthering Waves Game → …\Wuthering Waves）。</summary>
    private const int MaxAncestorLevels = 3;

    /// <summary>帧文件名中的序号（home_12.jpg → 12）；官方帧名不保证零填充，必须按数值比较。</summary>
    [GeneratedRegex(@"home_(\d+)\.jpg$", RegexOptions.IgnoreCase)]
    private static partial Regex FrameSequenceRegex();

    /// <summary>背景配置对象：库洛 switch.json 里与背景视频相关的一段 JSON（完整对象，可独立解析）。</summary>
    [GeneratedRegex(@"\{[^{}]*""backgroundFile"":""https?://[^""]+""[^{}]*\}")]
    private static partial Regex SwitchConfigRegex();

    /// <summary>配置请求 key 里的缓存破坏时间戳（…/switch.json?_t=1749488617），用于在多份历史响应间取最新。</summary>
    [GeneratedRegex(@"switch\.json\?_t=(\d+)")]
    private static partial Regex SwitchTimestampRegex();

    /// <summary>官启缓存根候选：Windows 取 %APPDATA%\KRLauncher 单根；Linux 逐 Wine/Proton prefix 探测。</summary>
    private static IReadOnlyList<string> DefaultCacheRoots() =>
        OperatingSystem.IsWindows() ? WindowsCacheRoots() : LinuxCacheRoots();

    /// <summary>Windows 官启数据目录（WebView2 缓存固定落在用户 %APPDATA% 下）。</summary>
    private static IReadOnlyList<string> WindowsCacheRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return appData.Length == 0 ? [] : [Path.Combine(appData, "KRLauncher")];
    }

    /// <summary>
    /// Linux 官启缓存根探测：官启是 Windows 程序，其 %APPDATA%\KRLauncher 位于 Wine/Proton
    /// prefix 的 drive_c/users/&lt;user&gt;/AppData/Roaming 下。纯 Wine prefix 取 $WINEPREFIX（优先）
    /// 与 ~/.wine；Proton prefix 逐 Steam 库 compatdata/&lt;appid&gt;/pfx 探测。只返回真实存在的目录。
    /// </summary>
    /// <param name="home">用户主目录；null = 真实主目录（测试注入临时目录）。</param>
    /// <param name="winePrefix">Wine prefix；null = 取 $WINEPREFIX 环境变量。</param>
    /// <param name="steamRoots">Steam 库根目录清单；null = CompatTools 的常见根目录。</param>
    internal static IReadOnlyList<string> LinuxCacheRoots(
        string? home = null, string? winePrefix = null, IReadOnlyList<string>? steamRoots = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        winePrefix ??= Environment.GetEnvironmentVariable("WINEPREFIX");

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(winePrefix))
        {
            AddPrefixKuroCacheRoots(roots, winePrefix);
        }

        AddPrefixKuroCacheRoots(roots, Path.Combine(home, ".wine"));

        foreach (var steamRoot in steamRoots ?? CompatTools.SteamRoots(home))
        {
            var compatData = Path.Combine(steamRoot, "steamapps", "compatdata");
            if (!Directory.Exists(compatData))
            {
                continue;
            }

            try
            {
                foreach (var appDir in Directory.EnumerateDirectories(compatData))
                {
                    AddPrefixKuroCacheRoots(roots, Path.Combine(appDir, "pfx"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个库目录不可读：跳过该库
            }
        }

        return roots;
    }

    /// <summary>把 prefix 内所有用户的 KRLauncher 数据目录加入候选（目录不存在则不加）。</summary>
    private static void AddPrefixKuroCacheRoots(List<string> roots, string prefix)
    {
        var usersDir = Path.Combine(prefix, "drive_c", "users");
        if (!Directory.Exists(usersDir))
        {
            return;
        }

        try
        {
            foreach (var user in Directory.EnumerateDirectories(usersDir))
            {
                var kuroData = Path.Combine(user, "AppData", "Roaming", "KRLauncher");
                if (Directory.Exists(kuroData))
                {
                    roots.Add(kuroData);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // users 目录不可读：跳过该 prefix
        }
    }

    /// <summary>扫描结果的本地持久化路径：Chromium 缓存会被 LRU 淘汰，最后一份已知配置留在我们这里兜底。</summary>
    private static string PersistedConfigPath => Path.Combine(AppPaths.ConfigDirectory, "kuro_bg_config.json");

    /// <summary>
    /// 扫描 KRLauncher 的 WebView 缓存，提取最后投放的背景配置（按缓存响应时间戳取最新）。
    /// 扫描不到时回退上次持久化的结果。找不到任何配置返回 null。
    /// </summary>
    /// <param name="cacheRoots">KRLauncher 数据根目录清单；null = 按平台自动探测（Windows %APPDATA% / Linux prefix）。</param>
    /// <param name="persistPath">持久化兜底文件路径；null = 应用数据目录下的默认路径。</param>
    public static KuroSwitchConfig? FindLatestSwitchConfig(
        IReadOnlyList<string>? cacheRoots = null, string? persistPath = null)
    {
        var roots = cacheRoots ?? DefaultCacheRoots();
        if (roots.Count > 0)
        {
            try
            {
                var match = WebViewCacheScanner.FindMatches(roots, SwitchConfigRegex(), SwitchTimestampRegex())
                    .OrderByDescending(m => m.NearbyTimestamp ?? long.MinValue)
                    .FirstOrDefault();
                if (match is not null && ParseSwitchConfig(match.Text) is { } config)
                {
                    PersistSwitchConfig(config, persistPath);
                    return config;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 缓存目录被占用/不可读：直接走持久化兜底
            }
        }

        return LoadPersistedSwitchConfig(persistPath);
    }

    /// <summary>把背景配置持久化到应用数据目录（缓存被 Chromium 淘汰后的兜底来源）；失败静默。</summary>
    /// <param name="config">要持久化的配置。</param>
    /// <param name="path">目标文件路径；null = 应用数据目录下的默认路径（测试注入临时文件）。</param>
    public static void PersistSwitchConfig(KuroSwitchConfig config, string? path = null)
    {
        var target = path ?? PersistedConfigPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, JsonSerializer.Serialize(config, PersistedJsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 持久化失败不致命：下次扫描重试
        }
    }

    /// <summary>读取上次持久化的背景配置；文件缺失或损坏返回 null。</summary>
    private static KuroSwitchConfig? LoadPersistedSwitchConfig(string? path = null)
    {
        try
        {
            var target = path ?? PersistedConfigPath;
            if (!File.Exists(target))
            {
                return null;
            }

            return JsonSerializer.Deserialize<KuroSwitchConfig>(
                File.ReadAllText(target), PersistedJsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>解析配置对象文本：backgroundFile 必有（正则保证），其余字段容缺失。</summary>
    private static KuroSwitchConfig? ParseSwitchConfig(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return KuroSwitchConfig.FromJson(doc.RootElement);
        }
        catch (JsonException)
        {
            // 缓存片段截断/拼接损坏：丢弃
            return null;
        }
    }

    private static readonly JsonSerializerOptions PersistedJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// 定位最新的背景帧。优先在游戏安装目录的兄弟目录（kr_game_cache）中查找，
    /// 再扫描各盘符的常见安装位置；返回动画末帧（帧序号最大，home_10 &gt; home_9），找不到返回 null。
    /// </summary>
    public static string? FindLatestFrame(string? gameInstallDir = null, IEnumerable<string>? driveRoots = null)
    {
        var latest = CandidateRoots(gameInstallDir, driveRoots)
            .Where(Directory.Exists)
            .SelectMany(EnumerateFrames)
            .OrderByDescending(FrameSequence)
            .FirstOrDefault();
        return latest;
    }

    /// <summary>帧文件名解析出的序号；无法解析的文件排最后。</summary>
    private static int FrameSequence(string framePath)
    {
        var match = FrameSequenceRegex().Match(framePath);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : -1;
    }

    private static IEnumerable<string> CandidateRoots(string? gameInstallDir, IEnumerable<string>? driveRoots)
    {
        // 游戏安装目录上溯：…\Wuthering Waves\Wuthering Waves Game → …\Wuthering Waves\kr_game_cache
        if (!string.IsNullOrWhiteSpace(gameInstallDir))
        {
            var dir = Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameInstallDir)));
            for (var i = 0; i < MaxAncestorLevels && dir is not null; i++)
            {
                yield return Path.Combine(dir, "kr_game_cache");
                dir = Path.GetDirectoryName(dir);
            }
        }

        // Windows 盘符布局兜底（\Wuthering Waves\kr_game_cache）；Linux 上退化为仅探测
        // <根>/Wuthering Waves/kr_game_cache，通常不命中——安装目录旁的上溯探测已覆盖主场景，
        // 保留为无害启发式
        foreach (var drive in driveRoots ?? DriveInfo.GetDrives().Select(d => d.Name))
        {
            yield return Path.Combine(drive, "Wuthering Waves", "kr_game_cache");
        }
    }

    private static IEnumerable<string> EnumerateFrames(string root)
    {
        try
        {
            // 立即物化：惰性枚举在后续排序时才抛 IOException/UnauthorizedAccessException 会逃出 try
            return [.. Directory.EnumerateFiles(
                Path.Combine(root, "animate_bg"), "home_*.jpg", SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>库洛启动器当期背景配置（来自 switch.json 或本地持久化）。</summary>
/// <param name="BackgroundFile">背景视频（mp4）CDN 直链。</param>
/// <param name="FirstFrameImage">首帧占位图（webp/png）直链，可缺省。</param>
/// <param name="Slogan">横幅图直链，可缺省。</param>
public sealed record KuroSwitchConfig(string BackgroundFile, string? FirstFrameImage, string? Slogan)
{
    /// <summary>从 switch.json 的对象元素解析背景配置；backgroundFile 缺失/空白（官方未投放背景）返回 null。</summary>
    public static KuroSwitchConfig? FromJson(JsonElement root)
    {
        var backgroundFile = GetStringOrNull(root, "backgroundFile");
        return string.IsNullOrWhiteSpace(backgroundFile)
            ? null
            : new KuroSwitchConfig(
                backgroundFile!,
                GetStringOrNull(root, "firstFrameImage"),
                GetStringOrNull(root, "slogan"));

        /// <summary>读取 JSON 对象的字符串字段；缺失或非字符串返回 null。</summary>
        static string? GetStringOrNull(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
