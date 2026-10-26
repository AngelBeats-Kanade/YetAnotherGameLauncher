using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>
/// 清单边界校验（2026-10-23）：官方清单是不可信输入，落盘前拒绝越界/禁写路径、
/// 文件与目录冲突、非法 MD5/尺寸。目前由库洛渠道在构造全量/增量/档位包清单时调用；
/// 消费端另有 <see cref="IsReservedPath"/> 作为废弃清单的纵深守卫。
/// </summary>
/// <remarks>
/// 两类"名单"语义不同，勿混：
/// <list type="bullet">
/// <item>本类的 <see cref="ReservedDirectories"/>/<see cref="ReservedFile"/> = <b>清单禁写/禁删</b>——
/// 不可信输入不得覆盖启动器状态、存档、官方启动器文件、Proton prefix。</item>
/// <item><see cref="GameManifest.PreservedPaths"/> 与 <c>GameInstallService.PreservedEntries</c> =
/// <b>同步收尾清理保留</b>——本启动器自己的清理策略。</item>
/// </list>
/// 两者有意不等：<c>launcherDownloadConfig</c>（官方启动器状态目录）对清单**只禁写/禁删**
/// （<see cref="IsReservedPath"/> 让 <c>deleteFiles</c> 也跳过它）、**不**列入清理保留，
/// 因为"清理策略保持现状、不兼容官方启动器"是 2026-10-23 的用户决定；混用官方启动器时该目录内容
/// 仍可能被同步收尾当游离文件删除，属已接受代价，不是缺陷。
/// </remarks>
public static class ManifestBoundary
{
    /// <summary>官方启动器状态目录（相对安装目录根）：不许清单写入/删除。</summary>
    public const string LauncherDownloadConfigDirectory = "launcherDownloadConfig";

    /// <summary>官方启动器状态文件：允许用户手动保留，但不得由游戏清单覆盖或删除。</summary>
    public const string ReservedFile = "launcherDownloadConfig.json";

    /// <summary>
    /// 不允许被清单写入/删除的目录（相对安装目录根）。清理保留表（GameInstallService.PreservedEntries）
    /// 由本表派生但<b>排除</b> <see cref="LauncherDownloadConfigDirectory"/>，见类注释的语义差异。
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedDirectories =
        [LocalStateService.StateDirName, "Saved", LauncherDownloadConfigDirectory, "compatdata"];

    /// <summary>
    /// 路径是否落在启动器状态/存档/官方启动器兼容目录内（含该目录本身）。
    /// 用于废弃清单的消费端守卫；路径本身的合法性由 <see cref="ValidateManifest"/> 负责。
    /// </summary>
    /// <param name="path">清单相对路径；null/空白按"不命中"处理（数据毛刺，由调用方跳过）。</param>
    /// <remarks>
    /// 判定前按落地语义词法归一（<c>.</c>/<c>..</c>/段尾点与空格），否则 <c>./Saved/x</c>、
    /// <c>foo/../Saved/x</c> 会绕过守卫而 <c>ResolveSafe</c> 仍落进 <c>Saved/</c>（2026-10-26 复审）。
    /// 逃出安装目录根的形态返回 false——那由 <c>ResolveSafe</c> 以 "escapes sandbox" 拒收。
    /// </remarks>
    public static bool IsReservedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var canonical = CanonicalizeWithinRoot(path);
        if (canonical is null)
        {
            return false;
        }

        var firstSlash = canonical.IndexOf('/');
        var firstSegment = firstSlash < 0 ? canonical : canonical[..firstSlash];
        if (string.Equals(firstSegment, ReservedFile, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ReservedDirectories.Contains(firstSegment, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 校验全量/增量清单：文件与组路径、MD5、尺寸、文件/目录冲突、禁写路径。
    /// 不安全条目一律抛 <see cref="UpdateException"/>（带来源，便于定位渠道）；
    /// 空白/重复条目按 F42 家族语义跳过（见各私有方法注释）。
    /// </summary>
    public static void ValidateManifest(GameManifest manifest, string source)
    {
        ValidateFiles(manifest.Files, source, "file");
        ValidateGroups(manifest.Groups, source);
        ValidateDeleteFiles(manifest.DeleteFiles, source);
    }

    /// <summary>校验档位包条目：必须全部落在该档位目录之下，且满足文件条目校验规则。</summary>
    public static void ValidatePackFiles(
        string packDirectory, IReadOnlyList<ManifestFile> files, string source)
    {
        var directory = NormalizeRelativePath(packDirectory, source, "pack directory").TrimEnd('/') + "/";
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                continue;
            }

            var path = NormalizeRelativePath(file.Path, source, "pack file");
            if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException(
                    $"{source}: pack file lies outside {packDirectory}: {file.Path}");
            }

            ValidateEntry(file, source, "pack file");
        }
    }

    private static void ValidateFiles(
        IReadOnlyList<ManifestFile> files, string source, string kind)
    {
        // 空白/重复条目跳过而非拒收：与 BuildResourceByDest/ToManifestFiles 的既有决定一致
        //（"清单不可信，F42 同族——单条畸形/冗余不得炸清单获取"）。重复 dest 在**单次**
        // ToManifestFiles 调用内先登记者胜；全量清单的基础/档位包两个列表天然不相交
        //（基础裁掉所有档位目录、包被限制在目标档目录下），故此处不再判定跨列表重复。
        // 大小写仅异的条目在 Linux 是不同文件、故意不合并（少收一条会让清理把它当游离文件删掉）。
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                continue;
            }

            var path = NormalizeRelativePath(file.Path, source, kind);
            if (IsReservedPath(path))
            {
                throw new UpdateException($"{source}: {kind} writes reserved path: {file.Path}");
            }

            ValidateEntry(file, source, kind);
            paths.Add(path);
        }

        // 文件与目录同名冲突：清单同时点名 `a` 与 `a/b` 时，落盘语义取决于写入顺序，
        // 两种结果都不安全（文件被目录覆盖或反之），直接拒收。
        foreach (var path in paths)
        {
            var slash = path.LastIndexOf('/');
            while (slash > 0)
            {
                var ancestor = path[..slash];
                if (paths.Contains(ancestor))
                {
                    throw new UpdateException(
                        $"{source}: {kind} path conflicts with a directory: {ancestor} / {path}");
                }

                slash = ancestor.LastIndexOf('/');
            }
        }
    }

    private static void ValidateGroups(IReadOnlyList<PatchGroup> groups, string source)
    {
        // 组的 PatchFile 是结构字段（渠道已滤掉空白 dest）：空白仍拒收当构造错误兜底；
        // 同名 PatchFile 则是真冲突——两个组下载同一个补丁文件到同一暂存路径，后到者覆盖先到者，
        // 两组都会拿错补丁。此处无 BuildResourceByDest 那样的先登记者胜先例，故拒收。
        var patchFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var patchFile = NormalizeRelativePath(group.PatchFile, source, "patch file");
            if (!patchFiles.Add(patchFile))
            {
                throw new UpdateException($"{source}: duplicate patch file: {group.PatchFile}");
            }

            if (group.PatchSize < 0)
            {
                throw new UpdateException($"{source}: negative patch size for {group.PatchFile}");
            }

            if (!string.IsNullOrEmpty(group.PatchMd5) && !IsMd5(group.PatchMd5))
            {
                throw new UpdateException($"{source}: invalid patch MD5 for {group.PatchFile}");
            }

            ValidateFiles(group.SrcFiles, source, "group source");
            ValidateFiles(group.DstFiles, source, "group destination");
        }
    }

    private static void ValidateDeleteFiles(IReadOnlyList<string> deleteFiles, string source)
    {
        // 删除清单是"尽力而为"清单：消费端对空白/缺失/目录/保留路径逐条跳过，只有**绝对/逃逸**
        // 这类明确的攻击面才拒收。目录条目（尾斜杠 `Old/`）、`./x`、`a/../x`、`a//b` 等别名由
        // 消费端归一后处理——在清单获取期拒收会让一次增量更新被永久挡住（增量链没有整包回退）。
        foreach (var path in deleteFiles)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var normalized = path.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Contains(':'))
            {
                throw new UpdateException($"{source}: absolute deleteFiles path rejected: {path}");
            }

            if (CanonicalizeWithinRoot(path) is null)
            {
                throw new UpdateException($"{source}: deleteFiles path escapes the install root: {path}");
            }
        }
    }

    private static void ValidateEntry(ManifestFile file, string source, string kind)
    {
        if (file.Size < 0)
        {
            throw new UpdateException($"{source}: negative size for {kind}: {file.Path}");
        }

        // 空 MD5 = 渠道未提供该字段的校验信息，与 ManifestVerifier.CheckFile/HttpFileDownloader
        // 的既有语义一致（IsNullOrEmpty，缺失时跳过对应校验而非恒判损坏）；空白串不算"缺失"
        //——它会被下游当"已提供但比不上"而永久失败，故按畸形值拒收（2026-10-26 复审）。
        if (!string.IsNullOrEmpty(file.Md5) && !IsMd5(file.Md5))
        {
            throw new UpdateException($"{source}: invalid MD5 for {kind}: {file.Path}");
        }
    }

    private static string NormalizeRelativePath(string path, string source, string kind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new UpdateException($"{source}: empty {kind} path");
        }

        var normalized = path.Replace('\\', '/');
        // 冒号按"绝对路径"一并拒收：既是 Windows 盘符（`C:`）与 NTFS 备用数据流（`file:stream`）
        // 的入口，也让 `foo:bar.pak` 这类 Linux 合法名变成"跨平台不可移植"。与段尾点/空格同一立场
        //（不可移植 → 拒收而非静默改名）；代价是整份清单取数失败，若真实清单出现此类名字需重新评估。
        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            throw new UpdateException($"{source}: absolute {kind} path rejected: {path}");
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                throw new UpdateException($"{source}: unsafe {kind} path rejected: {path}");
            }

            // Windows 建文件时会吃掉每段的尾点/尾空格（`Saved.` → `Saved`、`foo.pak.` → `foo.pak`），
            // 词法比较会因此漏判禁写路径（`Saved./x` 实际落进 Saved/）与重复路径（`x/pak` 与
            // `x/pak.` 并发写同一文件）。清单是外部输入、此类命名跨平台不可移植，直接拒收——
            // 既不静默改名（清理按原名找不到文件会误删），也不留绕过面（2026-10-23 复审）。
            if (segment[^1] is '.' or ' ')
            {
                throw new UpdateException(
                    $"{source}: {kind} path has a trailing dot/space segment (not portable): {path}");
            }
        }

        return normalized;
    }

    /// <summary>
    /// 按 <c>ResolveSafe</c> 的落地语义做词法归一（<c>/</c>、<c>.</c>、<c>..</c>、Windows 吃掉的
    /// 段尾点与空格）；<c>..</c> 弹出安装目录根时返回 null（逃逸交给 <c>ResolveSafe</c> 报错）。
    /// 消费端守卫用它把同一目标的不同写法（<c>./x</c>、<c>a/../x</c>、<c>Saved ./x</c>）折成一条串
    /// 再做前缀/产物/禁写三项判定（2026-10-26 复审）。
    /// </summary>
    /// <remarks>
    /// 判定顺序要紧：先只吃尾空格、识别 <c>.</c>/<c>..</c>（否则 <c>..</c> 会被尾点 trim 成空串、
    /// 父目录弹出永不触发，<c>data/../Saved/x</c> 因此漏判——2026-10-26 复审实测），再对真名连吃
    /// 尾点**与**尾空格（<c>"Saved ."</c> 只吃尾点会留下 <c>"Saved "</c>，Win32 归一后却是
    /// <c>Saved</c>，故两种字符要一起吃）。
    /// </remarks>
    public static string? CanonicalizeWithinRoot(string path)
    {
        var segments = new List<string>();
        foreach (var raw in path.Replace('\\', '/').Split('/'))
        {
            var trimmed = raw.TrimEnd(' ');
            if (trimmed is "" or ".")
            {
                continue;
            }

            if (trimmed == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            // Windows 建文件时连吃段尾点与空格（`Saved .` → `Saved`、`...` → 无效）
            var segment = trimmed.TrimEnd('.', ' ');
            if (segment == "")
            {
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    private static bool IsMd5(string value)
    {
        if (value.Length != 32)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
