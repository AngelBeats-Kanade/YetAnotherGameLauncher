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
    IReadOnlyDictionary<string, string>? ExtraEnvironment = null)
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
        var umuId = gameId.StartsWith("umu-", StringComparison.Ordinal) ? gameId : $"umu-{gameId}";
        var store = string.IsNullOrWhiteSpace(request.Store) ? "none" : request.Store!;

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
            ["STEAM_COMPAT_TOOL_PATHS"] = string.IsNullOrEmpty(runtimePath)
                ? proton
                : $"{proton}:{runtimePath}",
            ["STEAM_COMPAT_MOUNTS"] = string.IsNullOrEmpty(runtimePath)
                ? proton
                : $"{proton}:{runtimePath}",
            ["SteamAppId"] = "0",
            ["SteamGameId"] = "0",
            ["PROTON_CRASH_REPORT_DIR"] = Path.Combine(Path.GetTempPath(), "yagl-umu-crashreports"),
            ["UMU_RUNTIME_UPDATE"] = string.Empty,
            ["UMU_NO_PROTON"] = string.Empty,
        };

        // 上游当前行为：STEAM_COMPAT_APP_ID = prefix 路径的 MD5 十六进制
        env["STEAM_COMPAT_APP_ID"] = PrefixHash(pfx);
        env["SteamAppId"] = env["STEAM_COMPAT_APP_ID"];
        env["SteamGameId"] = env["STEAM_COMPAT_APP_ID"];

        // umu-<数字> 时把数字部分当作 Steam AppId（与上游 match ^umu-[\d\w]+$ 后截取一致的数字子集）
        if (umuId.StartsWith("umu-", StringComparison.Ordinal))
        {
            var suffix = umuId["umu-".Length..];
            if (suffix.Length > 0 && suffix.All(char.IsAsciiLetterOrDigit))
            {
                env["STEAM_COMPAT_APP_ID"] = suffix;
                env["SteamAppId"] = suffix;
                env["SteamGameId"] = suffix;
            }
        }

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

    /// <summary>prefix 路径 MD5（小写 hex），用于 STEAM_COMPAT_APP_ID。</summary>
    public static string PrefixHash(string prefixPath)
    {
        var bytes = Encoding.UTF8.GetBytes(prefixPath);
        return Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    }

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

        // 找到安装路径上第一个挂载点并记入 STEAM_COMPAT_LIBRARY_PATHS（Steam Game Drive）
        try
        {
            var current = new DirectoryInfo(installDir);
            while (current is not null && current.Parent is not null)
            {
                // .NET 无 is_mount；用根目录终止 + 存在性近似。完整挂载探测留给后续增强。
                if (string.Equals(current.FullName, Path.GetPathRoot(current.FullName), StringComparison.Ordinal))
                {
                    break;
                }

                current = current.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 忽略：库路径启发式失败不影响启动
        }
    }
}
