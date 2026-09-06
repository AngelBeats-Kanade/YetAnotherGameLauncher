using System.Text.Json;
using System.Text.Json.Serialization;

namespace YetAnotherGameLauncher.Channels.Hypergryph.Models;

/// <summary>GRYPHLINE batch_proxy 请求封装（协议字段名来自 LLauncher 逆向结果，保持原样）。</summary>
internal sealed class BatchProxyRequest
{
    [JsonPropertyName("seq")]
    public string Seq { get; set; } = "1";

    [JsonPropertyName("proxy_reqs")]
    public List<BatchProxyReq> ProxyReqs { get; set; } = [];
}

internal sealed class BatchProxyReq
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("get_latest_game_req")]
    public GetLatestGameReq? GetLatestGameReq { get; set; }
}

internal sealed class GetLatestGameReq
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("appcode")]
    public string Appcode { get; set; } = "";

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("sub_channel")]
    public string SubChannel { get; set; } = "";

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";
}

internal sealed class BatchProxyResponse
{
    [JsonPropertyName("proxy_rsps")]
    public List<JsonElement>? ProxyRsps { get; set; }
}

/// <summary>get_latest_game 响应：整包分发模型（packs 为压缩包列表）。</summary>
internal sealed class GameVersionResponse
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("action")]
    public int Action { get; set; }

    [JsonPropertyName("pkg")]
    public PackageInfo? Pkg { get; set; }

    /// <summary>预下载窗口期出现（下一版本的整包信息），其余为 null。</summary>
    [JsonPropertyName("patch")]
    public JsonElement? Patch { get; set; }
}

internal sealed class PackageInfo
{
    [JsonPropertyName("packs")]
    public List<PackFile> Packs { get; set; } = [];

    [JsonPropertyName("total_size")]
    public string TotalSize { get; set; } = "";

    [JsonPropertyName("game_files_md5")]
    public string GameFilesMd5 { get; set; } = "";

    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = "";
}

internal sealed class PackFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("md5")]
    public string Md5 { get; set; } = "";

    [JsonPropertyName("package_size")]
    public string PackageSize { get; set; } = "";
}
