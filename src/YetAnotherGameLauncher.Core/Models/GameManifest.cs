namespace YetAnotherGameLauncher.Core.Models;

/// <summary>清单中单个文件的校验信息。路径相对于游戏安装目录，统一使用 '/' 分隔。</summary>
public sealed record ManifestFile(
    string Path,
    long Size,
    string Md5,
    string? Url = null);

/// <summary>
/// 一个增量差分组（如库洛 krpdiff groupInfos）：把 SrcFiles 的当前内容
/// 经补丁器（hpatchz）处理后生成 DstFiles 的新内容。
/// </summary>
public sealed record PatchGroup(
    string PatchFile,
    long PatchSize,
    string? PatchMd5,
    IReadOnlyList<ManifestFile> SrcFiles,
    IReadOnlyList<ManifestFile> DstFiles,
    string? Url = null);

/// <summary>游戏文件清单，全量与增量共用。</summary>
public sealed class GameManifest
{
    /// <summary>该清单对应的目标版本号。</summary>
    public string Version { get; init; } = "";

    /// <summary>全量文件列表（增量清单中同时存在，作为合并后校验基准）。</summary>
    public IReadOnlyList<ManifestFile> Files { get; init; } = [];

    /// <summary>增量差分组；全量清单为空。</summary>
    public IReadOnlyList<PatchGroup> Groups { get; init; } = [];

    /// <summary>
    /// 增量清单点名的废弃文件（官方 deleteFiles，2026-10-02 真机实测顶层 6 条旧 pak/sig）：
    /// 应用更新（组循环与落位）之后删除——残留会被 UE 挂载覆盖新文件、热更卡死（ww-manager 同语义；
    /// 尾部时序为 2026-10-02 真机 P1 修订：先删会让 deleteFiles ∩ 组 srcFiles 形态永久不可行）。
    /// 全量清单与无该字段的旧暂存清单为空（反序列化缺省，向后兼容）。
    /// </summary>
    public IReadOnlyList<string> DeleteFiles { get; init; } = [];

    /// <summary>
    /// 包式渠道标记（如终末地）：Files 条目不是最终游戏文件，而是下载后需解压进安装目录的压缩包。
    /// </summary>
    public bool EntriesAreArchives { get; init; }

    /// <summary>
    /// 分卷归档标记（终末地 packs 实测形态，2026-10-17 国服/国际服 1.6.4 均 59 卷 ≈58 GiB）：
    /// Files 每个条目是同一归档的一片字节分卷（<c>…zip.001/002/…</c>），不是各自独立的压缩包——
    /// 单片不是合法 zip（中央目录与 EOCD 在末卷），逐片解压必报
    /// "End of Central Directory record could not be found"，必须先按 <see cref="Files"/> 顺序
    /// 拼接成一个归档再解压一次（Files 顺序 = 分卷顺序，渠道已按序号排好）。
    /// </summary>
    public bool ArchivesAreSplitVolumes { get; init; }
}
