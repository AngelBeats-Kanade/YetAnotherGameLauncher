namespace YetAnotherGameLauncher.Core.Dependencies;

/// <summary>一轮修复的结果统计。</summary>
/// <param name="Repaired">本次重链成功的悬空 builtin 链接数。</param>
/// <param name="Unrepairable">悬空但无法重链的数量（新 Proton 树缺对应文件或重写失败）；
/// 大于 0 即 prefix 仍不可被 wine 使用。</param>
public sealed record BuiltinRepairResult(int Repaired, int Unrepairable);

/// <summary>
/// Wine prefix 内置（builtin）组件链接修复。Proton 型 prefix 的 windows 根/system32/
/// syswow64/Fonts 下的 builtin 文件以符号链接接进创建它的 Proton 目录；该目录被升级清理
/// 删除后链接全部悬空，wine 任何 PE 进程启动即报 <c>could not load kernel32.dll, status
/// c0000135</c>（退出码 53）。两个入口对应两个时机：<see cref="RepairDangling"/> 是存量
/// 损坏的迁移路径（悬空链接重链到当前 Proton 树，依赖安装预检调用）；
/// <see cref="RepointTree"/> 是换版即时迁移（把指向旧 Proton 树的全部链接改指新树，兼容
/// 组件清理删除旧版之前调用——引用者先迁走，旧版才可删）。
/// 一律目标文件存在才写；有效链接（非本次范围）与真实文件不动，幂等可重入。
/// </summary>
public static class WinePrefixBuiltinRepair
{
    /// <summary>builtin 链接识别标记：目标含此段（Proton 根的 files/ 目录）即视为 builtin
    /// 组件链接，段后部分是在新旧 Proton 树之间的同相对路径键。取最后一个匹配——windows 树
    /// （lib/wine）、share/fonts、share/wine/fonts 等，以及 windows 之外的 Program Files
    /// （iexplore/wordpad/OLE DB 等），全部由此统一覆盖（真机实锤全 prefix 1337 条悬空横跨各处）。</summary>
    private const string ProtonFilesMarker = "/files/";

    /// <summary>扫描整个 prefix（递归），把全部悬空的 builtin 链接重链到
    /// 当前 Proton 树的同相对路径。</summary>
    /// <param name="prefixDirectory">Wine prefix 目录（WINEPREFIX 取值，含 drive_c）。</param>
    /// <param name="currentRoot">当前 Proton 根目录（含 files/）。</param>
    public static BuiltinRepairResult RepairDangling(string prefixDirectory, string currentRoot)
    {
        var repaired = 0;
        var unrepairable = 0;
        ForEachBuiltinLink(prefixDirectory, (link, target, isDangling) =>
        {
            if (!isDangling)
            {
                return;
            }

            if (TryRewriteLink(link, target, currentRoot))
            {
                repaired++;
            }
            else
            {
                unrepairable++;
            }
        });
        return new(repaired, unrepairable);
    }

    /// <summary>扫描整个 prefix（递归），把指向 <paramref name="oldRoot"/> 的全部 builtin
    /// 链接（无论有效还是已悬空）改指 <paramref name="newRoot"/> 的同相对路径——换版迁移：
    /// 清理删除旧 Proton 前先把引用它的 prefix 迁走，迁不干净（新树缺文件）由调用方保留旧版。</summary>
    /// <param name="prefixDirectory">Wine prefix 目录（WINEPREFIX 取值，含 drive_c）。</param>
    /// <param name="oldRoot">即将被删除的旧 Proton 根目录。</param>
    /// <param name="newRoot">接替的 Proton 根目录（含 files/）。</param>
    public static BuiltinRepairResult RepointTree(string prefixDirectory, string oldRoot, string newRoot)
    {
        var oldPrefix = oldRoot.EndsWith(Path.DirectorySeparatorChar)
            ? oldRoot
            : oldRoot + Path.DirectorySeparatorChar;
        var repaired = 0;
        var unrepairable = 0;
        ForEachBuiltinLink(prefixDirectory, (link, target, _) =>
        {
            // 只动指向旧树的链接；目标缺失（悬空）或有效都属迁移范围，相对路径键不变
            if (!target.StartsWith(oldPrefix, StringComparison.Ordinal))
            {
                return;
            }

            if (TryRewriteLink(link, target, newRoot))
            {
                repaired++;
            }
            else
            {
                unrepairable++;
            }
        });
        return new(repaired, unrepairable);
    }

    /// <summary>遍历整个 prefix 下的符号链接（手动栈式递归），对每个 builtin 链接回调：
    /// target 为链接目标（含 Proton files/ 标记才回调），isDangling 指目标当前是否不可解析。
    /// 只递归真实目录，绝不跟入符号链接目录——dosdevices/z: 指向 /，跟进会扫全盘并撞上
    /// /proc/&lt;死进程&gt;/cwd 之类的悬空目录链接（IOException "No such process"）。
    /// 非链接真实文件与目标不含标记的链接（非 Proton 布局）不回调。</summary>
    private static void ForEachBuiltinLink(
        string prefixDirectory, Action<string, string, bool> visit)
    {
        if (!Directory.Exists(prefixDirectory))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(prefixDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                directory, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    AttributesToSkip = 0,
                    IgnoreInaccessible = true,
                }))
            {
                string? target;
                try
                {
                    target = new FileInfo(entry).LinkTarget;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue; // 枚举与读取之间的竞态：该条目本轮跳过
                }

                if (target is not null)
                {
                    // 符号链接（无论指向文件还是目录）：只作候选、绝不递归进入
                    if (target.LastIndexOf(ProtonFilesMarker, StringComparison.Ordinal) >= 0)
                    {
                        visit(entry, target, IsDangling(entry));
                    }

                    continue;
                }

                if (new FileInfo(entry).Attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                }
            }
        }
    }

    /// <summary>链接是否悬空（目标不可解析）。不能用 <see cref="File.Exists"/> 判定——
    /// Unix 上它对悬空符号链接返回 true（lstat 语义，UmuPrefix.DeleteDanglingLink 同款
    /// 已知坑）；ResolveLinkTarget 在两种语义下都给出正确结果：断链返回 null，目标缺失
    /// 返回 Exists=false 的条目。</summary>
    private static bool IsDangling(string link)
    {
        var final = File.ResolveLinkTarget(link, returnFinalTarget: true);
        return final is null || !final.Exists;
    }

    /// <summary>测试缝：Delete 成功后、建链前调用（F50① 并发测试用它模拟并发修复者在
    /// 竞态窗内抢先改指新目标）；生产为 null 零开销。</summary>
    internal static Action<string>? AfterLinkDeleteForTests { get; set; }

    /// <summary>把链接重链到 root 下同相对路径（最后一个 /files/ 之后的部分不变）；
    /// 新目标存在才写（builtin 链接含目录形态——share/fonts 等段，须 File.Exists 与
    /// Directory.Exists 同查，F73）。并发修复撞空（Delete/建链竞态，F50①）：重查链接现状，
    /// 已被并发者改指新目标按已修复计——撞空伪报 Unrepairable 会误触发 PrefixUnhealthy；
    /// 仍非目标按未修复计（prune 侧保留旧版、安装器侧报可重试错误，兜底方向安全）。</summary>
    private static bool TryRewriteLink(string link, string oldTarget, string root)
    {
        var suffix = oldTarget[(oldTarget.LastIndexOf(ProtonFilesMarker, StringComparison.Ordinal)
            + ProtonFilesMarker.Length)..];
        var candidate = Path.Combine(root, "files", suffix);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return false;
        }

        try
        {
            File.Delete(link);
            AfterLinkDeleteForTests?.Invoke(link);
            File.CreateSymbolicLink(link, candidate);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AlreadyPointsAt(link, candidate);
        }
    }

    /// <summary>链接当前是否已解析到目标（并发修复撞空后的现状复核）。</summary>
    private static bool AlreadyPointsAt(string link, string candidate)
    {
        try
        {
            return File.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName
                == Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
