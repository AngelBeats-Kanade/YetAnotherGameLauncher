using System.Text.Json;
using System.Text.Json.Serialization;

namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>启动器自有 JSON 文档（games.json、state.json、staged manifest.json）统一序列化配置。</summary>
public static class Json
{
    public static JsonSerializerOptions Default { get; } = Create();

    private static JsonSerializerOptions Create() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
