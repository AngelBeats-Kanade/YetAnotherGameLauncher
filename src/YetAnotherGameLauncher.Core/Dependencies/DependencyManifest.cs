namespace YetAnotherGameLauncher.Core.Dependencies;

/// <summary>依赖清单里的单个字体文件与其要注册的全部族名（一个 ttc/ttf 可含多个族）。</summary>
/// <param name="File">字体文件名（仅文件名，落位到 prefix 的 windows/Fonts）。</param>
/// <param name="Families">要登记到 HKLM Fonts 键的族名列表。</param>
public sealed record DependencyFont(string File, IReadOnlyList<string> Families);

/// <summary>字体名替换组：把一组旧字体名（Windows 系统字体或应用字体别名）映射到目标字体（Wine Fonts\Replacements 语义）。</summary>
/// <param name="Target">提供实际字形的目标字体族名（须已在 Fonts 节登记）。</param>
/// <param name="Replaces">被替换的旧字体名列表（Windows 系统字体名或应用字体别名）。</param>
public sealed record DependencyReplacementGroup(string Target, IReadOnlyList<string> Replaces);

/// <summary>winetricks 来源安装的原生 DLL 条目（仅用于状态校验、备份、回滚与卸载，解包交给 winetricks）。</summary>
/// <param name="Arch">目标架构：x64 → system32，x86 → syswow64（仅 win64 prefix）。</param>
/// <param name="FileName">DLL 文件名（仅文件名，落位到对应系统目录）。</param>
/// <param name="SizeBytes">安装后期望的字节数（状态校验用）。</param>
/// <param name="Sha256">安装后期望的 SHA-256（64 位小写十六进制，状态校验用）。</param>
public sealed record DependencyDll(string Arch, string FileName, long SizeBytes, string Sha256);

/// <summary>
/// 依赖清单（声明式，内嵌于 Core 程序集）：描述一个可装入 Wine prefix 的组件。
/// 两种互斥来源：
/// ① 字体来源：下载 zip → 解压出字体文件 → 拷进 windows/Fonts → 注册表登记族名（HKLM Fonts）
/// 与旧名替换（HKCU Wine\Fonts\Replacements），与 Bottles cjkfonts.yml 的四类动作语义一一对应；
/// ② winetricks 来源：调用 winetricks 安装原生 DLL（如 msftedit），再写入 DllOverrides 覆盖；
/// 清单只声明安装后应得的 DLL 与覆盖，解包/下载由 winetricks 负责。
/// </summary>
/// <param name="Id">依赖标识（kebab-case，全局唯一；完成标记文件名由此派生）。</param>
/// <param name="Version">版本标识（上游版本或 YAGL 本地修订；写入完成标记，版本变更可检测重装）。</param>
/// <param name="DownloadUrl">压缩包下载地址（字体来源必填，必须 https）。</param>
/// <param name="FileName">下载缓存文件名（字体来源必填，仅文件名）。</param>
/// <param name="Md5">压缩包 MD5（字体来源必填，小写 hex，32 位）。</param>
/// <param name="SizeBytes">压缩包字节数（字体来源必填，下载器负责校验）。</param>
/// <param name="Fonts">要安装的字体文件（压缩包内纯文件名）与族名登记（字体来源必填）。</param>
/// <param name="ReplacementGroups">字体名替换组（字体来源必填；空数组合法）。</param>
/// <param name="WinetricksVerb">winetricks verb（winetricks 来源必填；与字体来源互斥）。</param>
/// <param name="Dlls">winetricks 安装后应存在的 DLL（winetricks 来源必填）。</param>
/// <param name="DllOverrides">要写入 HKCU\Software\Wine\DllOverrides 的覆盖（winetricks 来源必填）。</param>
public sealed record DependencyManifest(
    string Id,
    string Version,
    string? DownloadUrl,
    string? FileName,
    string? Md5,
    long? SizeBytes,
    IReadOnlyList<DependencyFont>? Fonts,
    IReadOnlyList<DependencyReplacementGroup>? ReplacementGroups,
    string? WinetricksVerb = null,
    IReadOnlyList<DependencyDll>? Dlls = null,
    IReadOnlyDictionary<string, string>? DllOverrides = null);
