using System.Text.Json.Serialization;

namespace YetAnotherGameLauncher.Channels.Hypergryph.Models;

/// <summary>
/// get_latest_game 的 patch 节点（v2 增量形态，2026-10-17 实测）：带本地版本请求时，
/// patch 指向"本地版本 → 最新版本"的更新补丁，v2_* 字段描述增量载荷与清单。
/// </summary>
internal sealed class EndfieldPatchNode
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("md5")]
    public string Md5 { get; set; } = "";

    [JsonPropertyName("package_size")]
    public string PackageSize { get; set; } = "";

    [JsonPropertyName("total_size")]
    public string TotalSize { get; set; } = "";

    [JsonPropertyName("patches")]
    public List<EndfieldPatchVolume>? Patches { get; set; }

    [JsonPropertyName("cd_key")]
    public string? CdKey { get; set; }

    [JsonPropertyName("v2_patch_info_url")]
    public string? V2PatchInfoUrl { get; set; }

    [JsonPropertyName("v2_verify_files_url")]
    public string? V2VerifyFilesUrl { get; set; }

    [JsonPropertyName("v2_verify_category_list")]
    public List<string>? V2VerifyCategoryList { get; set; }
}

/// <summary>增量载荷分卷（patches[] 条目）。</summary>
internal sealed class EndfieldPatchVolume
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("md5")]
    public string Md5 { get; set; } = "";

    [JsonPropertyName("package_size")]
    public string PackageSize { get; set; } = "";
}

/// <summary>v2 增量清单（v2_patch_info_url 的明文 JSON，实测 242 KB）。</summary>
internal sealed class EndfieldPatchManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("vfs_base_path")]
    public string VfsBasePath { get; set; } = "";

    [JsonPropertyName("files")]
    public List<EndfieldPatchManifestFile>? Files { get; set; }
}

/// <summary>清单里的一个目标文件节点。</summary>
internal sealed class EndfieldPatchManifestFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("md5")]
    public string Md5 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("diffType")]
    public int DiffType { get; set; }

    /// <summary>整文件复制形态：载荷内条目路径（实测与载荷条目名逐字符一致）。</summary>
    [JsonPropertyName("local_path")]
    public string? LocalPath { get; set; }

    /// <summary>差分形态：基文件与补丁条目（可多候选，2026-10-17 实测每个目标只有一条）。</summary>
    [JsonPropertyName("patch")]
    public List<EndfieldPatchManifestNode>? Patches { get; set; }
}

/// <summary>清单里的差分来源节点：基文件（版本过渡前的旧文件）与其 HDiffPatch 补丁。</summary>
internal sealed class EndfieldPatchManifestNode
{
    [JsonPropertyName("base_file")]
    public string BaseFile { get; set; } = "";

    [JsonPropertyName("base_md5")]
    public string BaseMd5 { get; set; } = "";

    [JsonPropertyName("base_size")]
    public long BaseSize { get; set; }

    /// <summary>补丁在载荷内的相对路径（相对 vfs_files/vfs_patch/，见渠道拼装）。</summary>
    [JsonPropertyName("patch")]
    public string Patch { get; set; } = "";

    [JsonPropertyName("patch_size")]
    public long PatchSize { get; set; }
}
