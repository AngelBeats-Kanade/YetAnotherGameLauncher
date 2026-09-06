using System.Text.Json.Serialization;

namespace YetAnotherGameLauncher.Channels.Kuro.Models;

/// <summary>库洛 launcher index.json 顶层结构（实测字段，参考 wutheringwaves-cli-manager）。</summary>
internal sealed class KuroLauncherIndex
{
    [JsonPropertyName("default")]
    public KuroResourceBlock? Default { get; set; }

    /// <summary>仅预下载窗口期出现且带 config；窗口期外为 null。</summary>
    [JsonPropertyName("predownload")]
    public KuroResourceBlock? Predownload { get; set; }

    [JsonPropertyName("predownloadSwitch")]
    public int PredownloadSwitch { get; set; }
}

/// <summary>index.json 中 default / predownload 共用的资源块。</summary>
internal sealed class KuroResourceBlock
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("cdnList")]
    public List<KuroCdnNode> CdnList { get; set; } = [];

    [JsonPropertyName("resourcesBasePath")]
    public string? ResourcesBasePath { get; set; }

    [JsonPropertyName("config")]
    public KuroResourceConfig? Config { get; set; }
}

/// <summary>index.json cdnList 中的一个 CDN 节点。</summary>
public sealed class KuroCdnNode
{
    [JsonPropertyName("P")]
    public int Priority { get; set; }

    [JsonPropertyName("K1")]
    public int K1 { get; set; }

    [JsonPropertyName("K2")]
    public int K2 { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

internal sealed class KuroResourceConfig
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("indexFile")]
    public string? IndexFile { get; set; }

    [JsonPropertyName("indexFileMd5")]
    public string? IndexFileMd5 { get; set; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("patchConfig")]
    public List<KuroPatchEntry>? PatchConfig { get; set; }
}

/// <summary>"从某旧版本 → 当前版本"的差分入口。</summary>
internal sealed class KuroPatchEntry
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("indexFile")]
    public string? IndexFile { get; set; }

    [JsonPropertyName("indexFileMd5")]
    public string? IndexFileMd5 { get; set; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }
}

/// <summary>游戏文件清单 indexFile.json。</summary>
internal sealed class KuroIndexFile
{
    [JsonPropertyName("resource")]
    public List<KuroResourceEntry> Resource { get; set; } = [];

    [JsonPropertyName("groupInfos")]
    public List<KuroGroupInfo>? GroupInfos { get; set; }
}

internal sealed class KuroResourceEntry
{
    [JsonPropertyName("dest")]
    public string Dest { get; set; } = "";

    [JsonPropertyName("md5")]
    public string Md5 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>部分条目指定独立资源目录（相对 CDN 根）。</summary>
    [JsonPropertyName("fromFolder")]
    public string? FromFolder { get; set; }

    [JsonPropertyName("chunkInfos")]
    public List<KuroChunkInfo>? ChunkInfos { get; set; }
}

internal sealed class KuroChunkInfo
{
    [JsonPropertyName("start")]
    public long Start { get; set; }

    /// <summary>闭区间终点。</summary>
    [JsonPropertyName("end")]
    public long End { get; set; }

    [JsonPropertyName("md5")]
    public string Md5 { get; set; } = "";
}

/// <summary>一个 krpdiff 差分组。</summary>
internal sealed class KuroGroupInfo
{
    [JsonPropertyName("dest")]
    public string Dest { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }

    [JsonPropertyName("srcFiles")]
    public List<KuroResourceEntry> SrcFiles { get; set; } = [];

    [JsonPropertyName("dstFiles")]
    public List<KuroResourceEntry> DstFiles { get; set; } = [];
}
