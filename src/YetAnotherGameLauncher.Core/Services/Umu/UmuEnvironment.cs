using System.Security.Cryptography;
using System.Text;

namespace YetAnotherGameLauncher.Core.Services.Umu;

/// <summary>原生 umu 启动所需的输入（由调用方解析游戏配置后传入）。</summary>
public sealed record UmuLaunchRequest(
    string GameId,
    string ExecutablePath,
    string InstallDir,
    string ProtonPath,
    string WinePrefix,
    SteamRuntimeInfo Runtime,
    string? Store = null,
    string ProtonVerb = "waitforexitandrun",
    IReadOnlyDictionary<string, string>? ExtraEnvironment = null,
    string? UmuId = null)
{
    /// <summary>默认 Proton 动词（与上游一致）。</summary>
    public const string DefaultVerb = "waitforexitandrun";
}

/// <summary>
/// 构建 umu/Proton 所需的完整环境变量（移植上游 check_env + set_env + enable_steam_game_drive 的核心）。
/// </summary>
public static class UmuEnvironment
{
    /// <summary>合法 Proton 动词集合。</summary>
    public static readonly IReadOnlySet<string> ProtonVerbs = new HashSet<string>(StringComparer.Ordinal)
    {
        "waitforexitandrun",
        "run",
        "runinprefix",
        "destroyprefix",
        "getcompatpath",
        "getnativepath",
    };

    /// <summary>
    /// 按请求生成环境字典。调用方应将其合并进进程环境；
    /// 与推荐链游戏项（SteamOS 等）冲突时由调用方决定覆盖顺序。
    /// </summary>
    public static Dictionary<string, string> Build(UmuLaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            throw new ArgumentException("游戏可执行文件路径为空。", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ProtonPath))
        {
            throw new ArgumentException("PROTONPATH 为空。", nameof(request));
        }

        var exe = Path.GetFullPath(request.ExecutablePath);
        var installDir = Path.GetFullPath(request.InstallDir);
        var pfx = Path.GetFullPath(request.WinePrefix);
        var proton = Path.GetFullPath(request.ProtonPath);
        var runtimePath = string.IsNullOrEmpty(request.Runtime.Variant)
            ? string.Empty
            : UmuPaths.RuntimeDirectory(request.Runtime.Variant);

        var verb = ProtonVerbs.Contains(request.ProtonVerb)
            ? request.ProtonVerb
            : UmuLaunchRequest.DefaultVerb;

        var gameId = string.IsNullOrWhiteSpace(request.GameId) ? "umu-default" : request.GameId;
        // UMU_ID 覆盖优先（games.json launch.umuId，对齐 umu 数据库规范 ID）；缺省 umu-{gameId}
        var umuId = !string.IsNullOrWhiteSpace(request.UmuId)
            ? EnsureUmuPrefix(request.UmuId)
            : EnsureUmuPrefix(gameId);
        var store = string.IsNullOrWhiteSpace(request.Store) ? "" : request.Store!;
        // Proton 与容器 Runtime 一并挂进容器（两个键取值与上游一致）
        var toolPaths = string.IsNullOrEmpty(runtimePath) ? proton : $"{proton}:{runtimePath}";

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GAMEID"] = umuId,
            ["UMU_ID"] = umuId,
            ["STORE"] = store,
            ["WINEPREFIX"] = pfx,
            ["STEAM_COMPAT_DATA_PATH"] = pfx,
            ["STEAM_COMPAT_SHADER_PATH"] = Path.Combine(pfx, "shadercache"),
            ["PROTONPATH"] = proton,
            ["PROTON_VERB"] = verb,
            ["EXE"] = exe,
            ["STEAM_COMPAT_INSTALL_PATH"] = installDir,
            ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam", "steam"),
            ["STEAM_COMPAT_TOOL_PATHS"] = toolPaths,
            ["STEAM_COMPAT_MOUNTS"] = toolPaths,
            ["PROTON_CRASH_REPORT_DIR"] = Path.Combine(Path.GetTempPath(), "yagl-umu-crashreports"),
            ["UMU_RUNTIME_UPDATE"] = string.Empty,
            ["UMU_NO_PROTON"] = string.Empty,
        };

        // 上游当前行为：STEAM_COMPAT_APP_ID 恒为 prefix 路径的 MD5 十六进制（SteamAppId/SteamGameId 取同值），
        // 即便 UMU_ID 后缀是纯数字（如 umu-3513350）也不用数字直通
        env["STEAM_COMPAT_APP_ID"] = PrefixHash(pfx);
        env["SteamAppId"] = env["STEAM_COMPAT_APP_ID"];
        env["SteamGameId"] = env["STEAM_COMPAT_APP_ID"];

        EnableSteamGameDrive(env, installDir);

        if (request.ExtraEnvironment is not null)
        {
            foreach (var (key, value) in request.ExtraEnvironment)
            {
                env[key] = value;
            }
        }

        return env;
    }

    /// <summary>补齐 umu- 前缀（UMU_ID 规范形式；已带前缀的原样保留）。</summary>
    private static string EnsureUmuPrefix(string id) =>
        id.StartsWith("umu-", StringComparison.Ordinal) ? id : $"umu-{id}";

    /// <summary>prefix 路径 MD5（小写 hex），用于 STEAM_COMPAT_APP_ID。</summary>
    public static string PrefixHash(string prefixPath)
    {
        var bytes = Encoding.UTF8.GetBytes(prefixPath);
        return Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// 移植上游 enable_steam_game_drive 的核心：STEAM_RUNTIME_LIBRARY_PATH = 现有 LD_LIBRARY_PATH + 安装目录，
    /// pressure-vessel 据此把游戏自带库挂进容器。上游基于 is_mount 的挂载点探测（STEAM_COMPAT_LIBRARY_PATHS）
    /// 需要读 /proc/mounts，这里不做——库路径注入只到上一步。
    /// </summary>
    private static void EnableSteamGameDrive(Dictionary<string, string> env, string installDir)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var ld = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (!string.IsNullOrEmpty(ld))
        {
            foreach (var part in ld.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                paths.Add(part);
            }
        }

        if (!string.IsNullOrEmpty(installDir))
        {
            paths.Add(installDir);
        }

        env["STEAM_RUNTIME_LIBRARY_PATH"] = string.Join(Path.PathSeparator, paths);
    }
}
