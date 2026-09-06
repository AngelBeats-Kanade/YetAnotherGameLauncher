using System.Text.Json;

namespace YetAnotherGameLauncher.Channels.Hypergryph;

/// <summary>GRYPHLINE 渠道共享的协议工具（版本接口与背景接口两处实现共用）。</summary>
internal static class GryphlineProtocol
{
    /// <summary>响应字段为 snake_case 的宽松反序列化选项。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>读 server.options 中的协议参数，空值回退缺省（国服/国际服切换依赖此行为）。</summary>
    public static string OptionOrDefault(IReadOnlyDictionary<string, string> options, string key, string fallback) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
}
