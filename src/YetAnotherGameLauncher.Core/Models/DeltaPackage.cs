namespace YetAnotherGameLauncher.Core.Models;

/// <summary>v2 增量载荷的分卷（官方 patches[]：URL + 尺寸 + MD5，共 9 卷 ≈8.72 GiB）。</summary>
public sealed record DeltaPackageVolume(string Url, long Size, string Md5);

/// <summary>v2 增量载荷的差分来源节点（官方 patch.json 的 files[].patch[]）：
/// 基文件（版本过渡前的旧文件）与它的 HDiffPatch 单文件补丁在载荷内的路径。</summary>
public sealed record DeltaPatchSource(string BaseFile, string BaseMd5, long BaseSize, string PatchPath, long PatchSize);

/// <summary>
/// v2 增量载荷的单个目标文件节点（官方 patch.json 的 files[]）：
/// 目标是"整文件复制"（<paramref name="FullFilePayloadPath"/> 指向载荷条目）或
/// "差分合成"（<paramref name="Patches"/> 非空）或两者皆无 = 只要求文件已就位（verify-only，
/// 2026-10-17 实测 717/1138 条：盘上 md5 与清单一致）。
/// </summary>
public sealed record DeltaPackageFile(
    string Name,
    string Md5,
    long Size,
    int DiffType,
    string? FullFilePayloadPath,
    IReadOnlyList<DeltaPatchSource> Patches);

/// <summary>
/// v2 增量更新计划（渠道解析产出的合同）：分卷 + 口令（cd_key）+ 文件节点清单 + VFS 基路径
/// + 静态条目归类规则（载荷里除 VFS 节点外的整文件条目直接覆盖游戏根，含 dll/exe 等）。
/// 载荷条目（含 delete_files.txt）都在这几卷 zip 里，读取由 <c>DeltaPackageApplier</c> 负责。
/// </summary>
public sealed record DeltaPackagePlan(
    string FromVersion,
    string ToVersion,
    string VfsBasePath,
    string CdKey,
    IReadOnlyList<DeltaPackageVolume> Volumes,
    IReadOnlyList<DeltaPackageFile> Files,
    IReadOnlyList<string> StaticEntryExcludedPrefixes,
    IReadOnlyList<string> StaticEntryExcludedNames,
    IReadOnlyDictionary<string, string> StaticEntryRewrites);
