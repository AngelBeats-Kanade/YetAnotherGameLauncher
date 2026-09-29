using Xunit;
using YetAnotherGameLauncher.Core.Dependencies;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Dependencies;

/// <summary>
/// Wine prefix 内置组件链接修复：悬空链接重链到当前 Proton 树、有效链接/真实文件不动、
/// 新树缺文件的悬空链接计为不可修、幂等重入。真实符号链接的文件系统行为——Windows 无特权
/// 建链，跳过（CI 与本机开发均为 Linux）。
/// </summary>
public class WinePrefixBuiltinRepairTests : IDisposable
{
    private readonly TempDir _temp = new();

    /// <summary>构造一棵最小 Proton 树（files/lib/wine/&lt;arch&gt;/&lt;files&gt;）。</summary>
    private string MakeProtonTree(string name, string arch, params string[] files)
    {
        var root = _temp.FilePath(name);
        foreach (var file in files)
        {
            var path = Path.Combine(root, "files", "lib", "wine", arch, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "builtin");
        }

        return root;
    }

    /// <summary>建 prefix 的 builtin 链接区（system32/syswow64 下的符号链接）。</summary>
    private string MakePrefixLink(string arch, string file, string linkTarget)
    {
        var link = Path.Combine(
            _temp.FilePath("prefix"), "drive_c", "windows", arch, file);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, linkTarget);
        return link;
    }

    /// <summary>在 prefix 内按相对路径（drive_c 下）建符号链接。</summary>
    private string MakePrefixLinkAt(string relativePath, string linkTarget)
    {
        var link = Path.Combine(_temp.FilePath("prefix"), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, linkTarget);
        return link;
    }

    /// <summary>造一棵带 builtin 文件的"旧 Proton"，建链后整树删除使其链接悬空，返回原目标路径。</summary>
    private string MakeDanglingTarget(string prefixArch, string prefixFile, string arch, string file)
    {
        var oldRoot = MakeProtonTree("proton-old", arch, file);
        var oldPath = Path.Combine(oldRoot, "files", "lib", "wine", arch, file);
        MakePrefixLink(prefixArch, prefixFile, oldPath);
        Directory.Delete(oldRoot, recursive: true);
        return oldPath;
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Repair_DanglingBuiltinLinks_RelinksIntoCurrentTree()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
        }

        var oldKernel = MakeDanglingTarget("system32", "kernel32.dll", "x86_64-windows", "kernel32.dll");
        var oldUser = MakeDanglingTarget("syswow64", "user32.dll", "i386-windows", "user32.dll");
        var newRoot = MakeProtonTree("proton-new", "x86_64-windows", "kernel32.dll");
        MakeProtonTree("proton-new", "i386-windows", "user32.dll");

        var result = WinePrefixBuiltinRepair.RepairDangling(_temp.FilePath("prefix"), newRoot);

        Assert.Equal(2, result.Repaired);
        Assert.Equal(0, result.Unrepairable);
        // 链接已改指新树同相对路径，且经链接可解析到文件
        Assert.Equal(
            Path.Combine(newRoot, "files", "lib", "wine", "x86_64-windows", "kernel32.dll"),
            new FileInfo(Path.Combine(
                    _temp.FilePath("prefix"), "drive_c", "windows", "system32", "kernel32.dll")).LinkTarget);
        Assert.Equal(
            Path.Combine(newRoot, "files", "lib", "wine", "i386-windows", "user32.dll"),
            new FileInfo(Path.Combine(
                    _temp.FilePath("prefix"), "drive_c", "windows", "syswow64", "user32.dll")).LinkTarget);
        Assert.True(File.Exists(Path.Combine(
            _temp.FilePath("prefix"), "drive_c", "windows", "system32", "kernel32.dll")));
        Assert.False(File.Exists(oldKernel));
        Assert.False(File.Exists(oldUser));
    }

    [Fact]
    public void Repair_ValidLinksAndRealFiles_LeftUntouched()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
        }

        var newRoot = MakeProtonTree("proton-new", "x86_64-windows", "kernel32.dll");
        var validTarget = Path.Combine(newRoot, "files", "lib", "wine", "x86_64-windows", "kernel32.dll");
        var validLink = MakePrefixLink("system32", "kernel32.dll", validTarget);
        var foreignTarget = _temp.FilePath("elsewhere", "dll.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(foreignTarget)!);
        File.WriteAllText(foreignTarget, "x");
        var foreignLink = MakePrefixLink("system32", "foreign.dll", foreignTarget);
        var realFile = Path.Combine(
            _temp.FilePath("prefix"), "drive_c", "windows", "system32", "real.dll");
        File.WriteAllText(realFile, "real");

        var result = WinePrefixBuiltinRepair.RepairDangling(_temp.FilePath("prefix"), newRoot);

        // 健康区零触碰：有效链接（含非 Proton 布局的目标）与真实文件都不是修复对象
        Assert.Equal(0, result.Repaired);
        Assert.Equal(0, result.Unrepairable);
        Assert.Equal(validTarget, new FileInfo(validLink).LinkTarget);
        Assert.Equal(foreignTarget, new FileInfo(foreignLink).LinkTarget);
        Assert.True(File.Exists(realFile));
        Assert.Null(new FileInfo(realFile).LinkTarget);
    }

    [Fact]
    public void Repair_DanglingDirectoryTypeBuiltinLink_RelinksIntoCurrentTree()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
        }

        // F73：builtin 链接含目录形态（类注释自述覆盖 files/share/fonts 等目录段）——
        // TryRewriteLink 原用 File.Exists 验证新目标，File.Exists 对目录恒 false → 目录型
        // 链接恒判 Unrepairable，依赖安装预检误报 PrefixUnhealthy 而链接本身可重建。
        // 旧树含目录型 builtin + 悬空链接，新树同相对路径目录在位 → 应重链成功
        var oldRoot = _temp.FilePath("proton-old");
        var oldDir = Path.Combine(oldRoot, "files", "share", "fonts", "truetype", "some-family");
        Directory.CreateDirectory(oldDir);
        var link = MakePrefixLinkAt(
            Path.Combine("drive_c", "windows", "Fonts", "some-family"),
            oldDir);
        Directory.Delete(oldRoot, recursive: true);
        var newRoot = _temp.FilePath("proton-new");
        Directory.CreateDirectory(Path.Combine(newRoot, "files", "share", "fonts", "truetype", "some-family"));

        var result = WinePrefixBuiltinRepair.RepairDangling(_temp.FilePath("prefix"), newRoot);

        Assert.Equal(1, result.Repaired);
        Assert.Equal(0, result.Unrepairable);
        Assert.Equal(
            Path.Combine(newRoot, "files", "share", "fonts", "truetype", "some-family"),
            new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Repair_DanglingLinkMissingInNewTree_CountsUnrepairableAndLeavesLink()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
        }

        var oldPath = MakeDanglingTarget("system32", "kernel32.dll", "x86_64-windows", "kernel32.dll");
        var newRoot = MakeProtonTree("proton-new", "x86_64-windows", "d3d9.dll"); // 新树缺 kernel32

        var result = WinePrefixBuiltinRepair.RepairDangling(_temp.FilePath("prefix"), newRoot);

        Assert.Equal(0, result.Repaired);
        Assert.Equal(1, result.Unrepairable);
        Assert.Equal(oldPath, new FileInfo(Path.Combine(
            _temp.FilePath("prefix"), "drive_c", "windows", "system32", "kernel32.dll")).LinkTarget);
    }

    [Fact]
    public void Repair_SecondRun_Idempotent()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
        }

        MakeDanglingTarget("system32", "kernel32.dll", "x86_64-windows", "kernel32.dll");
        var newRoot = MakeProtonTree("proton-new", "x86_64-windows", "kernel32.dll");

        var first = WinePrefixBuiltinRepair.RepairDangling(_temp.FilePath("prefix"), newRoot);
        var second = WinePrefixBuiltinRepair.RepairDangling(_temp.FilePath("prefix"), newRoot);

        Assert.Equal(1, first.Repaired);
        Assert.Equal(0, second.Repaired);
        Assert.Equal(0, second.Unrepairable);
    }

    [Fact]
    public void Repair_ScansWholePrefix_IncludingShareFontsAndNestedDirs()
    {
        // builtin 链接不止 system32/syswow64 的根层文件：windows 根（regedit 等 shell 程序）、
        // windows/Fonts（指向 Proton 的 files/share/fonts 与 files/share/wine/fonts）、
        // 嵌套子目录（command/start.exe 等）——真机实锤共 120 条悬空、遍布 windows 树
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
        }

        var oldRoot = _temp.FilePath("proton-old");
        var targets = new[]
        {
            Path.Combine(oldRoot, "files", "lib", "wine", "x86_64-windows", "regedit.exe"),
            Path.Combine(oldRoot, "files", "share", "fonts", "simsun.ttc"),
            Path.Combine(oldRoot, "files", "lib", "wine", "x86_64-windows", "start.exe"),
        };
        foreach (var target in targets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "x");
        }

        var exeLink = MakePrefixLinkAt(
            Path.Combine("drive_c", "windows", "regedit.exe"), targets[0]);
        var fontLink = MakePrefixLinkAt(
            Path.Combine("drive_c", "windows", "Fonts", "simsun.ttc"), targets[1]);
        var nestedLink = MakePrefixLinkAt(
            Path.Combine("drive_c", "windows", "command", "start.exe"), targets[2]);
        Directory.Delete(oldRoot, recursive: true);

        var newRoot = _temp.FilePath("proton-new");
        foreach (var rel in new[]
                 {
                     "files/lib/wine/x86_64-windows/regedit.exe",
                     "files/share/fonts/simsun.ttc",
                     "files/lib/wine/x86_64-windows/start.exe",
                 })
        {
            var path = Path.Combine(newRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }

        var result = WinePrefixBuiltinRepair.RepairDangling(_temp.FilePath("prefix"), newRoot);

        Assert.Equal(3, result.Repaired);
        Assert.Equal(0, result.Unrepairable);
        Assert.True(File.Exists(exeLink));
        Assert.True(File.Exists(fontLink));
        Assert.True(File.Exists(nestedLink));
    }

    [Fact]
    public void Repair_ScansBeyondWindowsTree_ProgramFilesLinksToo()
    {
        // builtin 链接不止 windows 树：Program Files 下的 iexplore/wordpad/OLE DB 等同样
        // 链接进 Proton 树（真机实锤 14 条）——扫描范围必须是整个 prefix
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
        }

        var oldTarget = Path.Combine(
            _temp.FilePath("proton-old"), "files", "lib", "wine", "i386-windows", "wordpad.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(oldTarget)!);
        File.WriteAllText(oldTarget, "x");
        var link = MakePrefixLinkAt(
            Path.Combine("drive_c", "Program Files (x86)", "Windows NT", "Accessories", "wordpad.exe"),
            oldTarget);
        Directory.Delete(_temp.FilePath("proton-old"), recursive: true);
        var newTarget = Path.Combine(
            _temp.FilePath("proton-new"), "files", "lib", "wine", "i386-windows", "wordpad.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(newTarget)!);
        File.WriteAllText(newTarget, "x");

        var result = WinePrefixBuiltinRepair.RepairDangling(_temp.FilePath("prefix"), _temp.FilePath("proton-new"));

        Assert.Equal(1, result.Repaired);
        Assert.Equal(0, result.Unrepairable);
        Assert.True(File.Exists(link));
    }

    [Fact]
    public void Repair_NeverRecursesIntoSymlinkedDirectories()
    {
        // dosdevices/z: 指向 /：递归扫描绝不能跟入符号链接目录——真机实锤跟进 z: 后
        // 撞上 /proc/<死进程>/cwd 直接 IOException 崩掉整轮修复（wine 布局的常规形态）。
        // F47 变异实验：只放"悬空的 z:"杀不掉"跟入存活符号链接目录"的变异体——所以
        // z: 指向**存活**的外部目录并放一条 builtin 形态的悬空链接，跟进它的实现必然
        // 把外部链接计入修复统计，正确实现只见 prefix 本体（0/0）
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
        }

        var outside = _temp.FilePath("outside");
        var externalTarget = Path.Combine(outside, "files", "lib", "wine", "x86_64-windows", "fake.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(externalTarget)!);
        File.WriteAllText(externalTarget, "x");
        var externalLink = Path.Combine(outside, "link.dll");
        File.CreateSymbolicLink(externalLink, externalTarget);
        File.Delete(externalTarget); // 外部悬空链接就绪；outside 本体保持存活
        MakePrefixLinkAt(Path.Combine("dosdevices", "z:"), outside);
        var newRoot = MakeProtonTree("proton-new", "x86_64-windows", "kernel32.dll");

        var result = WinePrefixBuiltinRepair.RepairDangling(_temp.FilePath("prefix"), newRoot);

        Assert.Equal(0, result.Repaired); // 红落此断言：跟入 z: 的实现会计入外部悬空链接
        Assert.Equal(0, result.Unrepairable);
    }

    [Fact]
    public void Repair_PrefixDirectoryMissing_ReturnsZeroZero()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
        }

        var newRoot = MakeProtonTree("proton-new", "x86_64-windows", "kernel32.dll");

        var result = WinePrefixBuiltinRepair.RepairDangling(_temp.FilePath("no-such-prefix"), newRoot);

        Assert.Equal(0, result.Repaired);
        Assert.Equal(0, result.Unrepairable);
    }

    [Fact]
    public void RepointTree_LinksUnderOldTree_ValidOrDangling_AllRepointed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
        }

        // 旧树存在（prune 候选，删除前迁移）：一条有效链接 + 一条悬空链接（目标文件在
        // oldRoot 之内建后单删，旧树本体保留）
        var oldRoot = MakeProtonTree("proton-old", "x86_64-windows", "kernel32.dll");
        var validLink = MakePrefixLink("system32", "kernel32.dll", Path.Combine(
            oldRoot, "files", "lib", "wine", "x86_64-windows", "kernel32.dll"));
        var userFile = Path.Combine(oldRoot, "files", "lib", "wine", "i386-windows", "user32.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        File.WriteAllText(userFile, "x");
        var danglingLink = MakePrefixLink("syswow64", "user32.dll", userFile);
        File.Delete(userFile);
        var newRoot = MakeProtonTree("proton-new", "x86_64-windows", "kernel32.dll");
        MakeProtonTree("proton-new", "i386-windows", "user32.dll");

        var result = WinePrefixBuiltinRepair.RepointTree(_temp.FilePath("prefix"), oldRoot, newRoot);

        // 有效与悬空同迁：2 条全部改指新树；旧树本体不动（是否删除由调用方决定）
        Assert.Equal(2, result.Repaired);
        Assert.Equal(0, result.Unrepairable);
        Assert.Equal(
            Path.Combine(newRoot, "files", "lib", "wine", "x86_64-windows", "kernel32.dll"),
            new FileInfo(validLink).LinkTarget);
        Assert.Equal(
            Path.Combine(newRoot, "files", "lib", "wine", "i386-windows", "user32.dll"),
            new FileInfo(danglingLink).LinkTarget);
        Assert.True(Directory.Exists(oldRoot));
    }

    [Fact]
    public void RepointTree_LinkTargetingOtherTree_LeftAlone()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
        }

        var thirdRoot = MakeProtonTree("proton-third", "x86_64-windows", "kernel32.dll");
        var otherLink = MakePrefixLink("system32", "kernel32.dll", Path.Combine(
            thirdRoot, "files", "lib", "wine", "x86_64-windows", "kernel32.dll"));
        var oldRoot = MakeProtonTree("proton-old", "x86_64-windows", "kernel32.dll");
        var newRoot = MakeProtonTree("proton-new", "x86_64-windows", "kernel32.dll");

        var result = WinePrefixBuiltinRepair.RepointTree(_temp.FilePath("prefix"), oldRoot, newRoot);

        Assert.Equal(0, result.Repaired);
        Assert.Equal(0, result.Unrepairable);
        Assert.Equal(
            Path.Combine(thirdRoot, "files", "lib", "wine", "x86_64-windows", "kernel32.dll"),
            new FileInfo(otherLink).LinkTarget);
    }

    [Fact]
    public void RepointTree_NewTreeMissingFile_CountsUnrepairableAndKeepsOldLink()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
        }

        // 新树缺对应文件：迁不动 → 调用方（prune）必须保留旧版，链接保持指向旧树
        var oldRoot = MakeProtonTree("proton-old", "x86_64-windows", "kernel32.dll");
        var link = MakePrefixLink("system32", "kernel32.dll", Path.Combine(
            oldRoot, "files", "lib", "wine", "x86_64-windows", "kernel32.dll"));
        var newRoot = MakeProtonTree("proton-new", "x86_64-windows", "d3d9.dll");

        var result = WinePrefixBuiltinRepair.RepointTree(_temp.FilePath("prefix"), oldRoot, newRoot);

        Assert.Equal(0, result.Repaired);
        Assert.Equal(1, result.Unrepairable);
        Assert.Equal(
            Path.Combine(oldRoot, "files", "lib", "wine", "x86_64-windows", "kernel32.dll"),
            new FileInfo(link).LinkTarget);
    }
}
