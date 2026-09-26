namespace YetAnotherGameLauncher.Core.Dependencies;

/// <summary>依赖清单里的单个字体文件与其要注册的全部族名（一个 ttc/ttf 可含多个族）。</summary>
/// <param name="File">字体文件名（仅文件名，落位到 prefix 的 windows/Fonts）。</param>
/// <param name="Families">要登记到 HKLM Fonts 键的族名列表。</param>
public sealed record DependencyFont(string File, IReadOnlyList<string> Families);

/// <summary>字体名替换组：把一组旧字体名（Windows 系统字体）映射到目标字体（Wine Fonts\Replacements 语义）。</summary>
/// <param name="Target">提供实际字形的目标字体族名（须已在 Fonts 节登记）。</param>
/// <param name="Replaces">被替换的旧字体名列表。</param>
public sealed record DependencyReplacementGroup(string Target, IReadOnlyList<string> Replaces);

/// <summary>
/// 依赖清单（声明式，内嵌于 Core 程序集）：描述一个可装入 Wine prefix 的组件。
/// 首版 schema 面向字体类依赖：下载 zip → 解压出字体文件 → 拷进 windows/Fonts →
/// 注册表登记族名（HKLM Fonts）与旧名替换（HKCU Wine\Fonts\Replacements）——
/// 与 Bottles cjkfonts.yml 的四类动作语义一一对应，但合并为单次 reg import。
/// </summary>
/// <param name="Id">依赖标识（kebab-case，全局唯一；完成标记文件名由此派生）。</param>
/// <param name="Version">上游版本标识（写入完成标记，版本变更可检测重装）。</param>
/// <param name="DownloadUrl">压缩包下载地址（必须 https）。</param>
/// <param name="FileName">下载缓存文件名（仅文件名，落共享缓存目录）。</param>
/// <param name="Md5">压缩包 MD5（小写 hex，32 位；下载器负责校验）。</param>
/// <param name="SizeBytes">压缩包字节数（下载器负责校验）。</param>
/// <param name="Fonts">要安装的字体文件（压缩包内纯文件名）与族名登记。</param>
/// <param name="ReplacementGroups">字体名替换组。</param>
public sealed record DependencyManifest(
    string Id,
    string Version,
    string DownloadUrl,
    string FileName,
    string Md5,
    long SizeBytes,
    IReadOnlyList<DependencyFont> Fonts,
    IReadOnlyList<DependencyReplacementGroup> ReplacementGroups);
