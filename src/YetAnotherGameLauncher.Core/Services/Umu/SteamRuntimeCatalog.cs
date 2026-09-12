using System.Globalization;

namespace YetAnotherGameLauncher.Core.Services.Umu;

/// <summary>Steam Linux Runtime 描述（与上游 umu RUNTIME_VERSIONS 对齐）。</summary>
/// <param name="Name">逻辑名（soldier/sniper/steamrt4…）。</param>
/// <param name="Variant">目录变体名（steamrt2/steamrt3/steamrt4…）。</param>
/// <param name="AppId">Steam 工具 appid（toolmanifest 的 require_tool_appid）。</param>
/// <param name="Machine">目标机器架构。</param>
public sealed record SteamRuntimeInfo(string Name, string Variant, string AppId, string Machine);

/// <summary>Steam Runtime 目录与 appid 的映射表（单一事实源，供 toolmanifest 解析回查）。</summary>
public static class SteamRuntimeCatalog
{
    /// <summary>host：不使用容器运行时（passthrough）。</summary>
    public static readonly SteamRuntimeInfo Host = new("host", "", "", "x86_64");

    /// <summary>已知 Runtime 表，键为 Steam appid。</summary>
    public static readonly IReadOnlyDictionary<string, SteamRuntimeInfo> ByAppId =
        new Dictionary<string, SteamRuntimeInfo>(StringComparer.Ordinal)
        {
            ["1391110"] = new("soldier", "steamrt2", "1391110", "x86_64"),
            ["1628350"] = new("sniper", "steamrt3", "1628350", "x86_64"),
            ["4183110"] = new("steamrt4", "steamrt4", "4183110", "x86_64"),
            ["4185400"] = new("steamrt4-arm64", "steamrt4-arm64", "4185400", "aarch64"),
        };

    /// <summary>按 appid 解析 Runtime；未知 appid 返回 null。</summary>
    public static SteamRuntimeInfo? FromAppId(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        return ByAppId.TryGetValue(appId.Trim(), out var info) ? info : null;
    }

    /// <summary>无 require_tool_appid 时的默认 Runtime（现代 Proton 走 steamrt4）。</summary>
    public static SteamRuntimeInfo Default => ByAppId["4183110"];

    /// <summary>
    /// 归档文件名：SteamLinuxRuntime_sniper.tar.xz / SteamLinuxRuntime_steamrt4.tar.xz。
    /// steamrt 前缀数字变体去掉 steamrt 前缀后拼文件名。
    /// </summary>
    public static string ArchiveFileName(SteamRuntimeInfo runtime)
    {
        var codename = runtime.Name;
        if (codename.StartsWith("steamrt", StringComparison.Ordinal) &&
            codename["steamrt".Length..].TrimEnd('-').All(char.IsDigit))
        {
            // steamrt4 → SteamLinuxRuntime_4；steamrt4-arm64 → SteamLinuxRuntime_4
            var digits = new string(codename.Skip("steamrt".Length).TakeWhile(char.IsDigit).ToArray());
            return FormattableString.Invariant($"SteamLinuxRuntime_{digits}.tar.xz");
        }

        return FormattableString.Invariant($"SteamLinuxRuntime_{codename}.tar.xz");
    }

    /// <summary>下载端点路径前缀：/{variant去掉-arm64}/images（与上游一致）。</summary>
    public static string ImagesPathPrefix(SteamRuntimeInfo runtime) =>
        $"/{runtime.Variant.Replace("-arm64", string.Empty, StringComparison.Ordinal)}/images";
}
