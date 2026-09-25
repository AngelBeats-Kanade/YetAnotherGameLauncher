using Xunit;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>
/// FileUtilities.TryDeleteDirectory 的 best-effort 契约（F25，artifacts/bugs.md）：
/// 方法注释自述"能删多少删多少"，但顶层枚举是方法内唯一裸 I/O——子树含无 ListDirectory
/// 权限的目录（ACL 损坏/安全软件 deny）时裸 UnauthorizedAccessException 打断整链。
/// 修复 = 枚举 IgnoreInaccessible（官方语义：跳过 AccessDenied 子项）+ 顶层 catch 返回 false
/// （path 本身不可读时 IgnoreInaccessible 管不住，官方文档明示其只作用于枚举到的子项）。
/// </summary>
public class FileUtilitiesTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static void Chmod(string path, UnixFileMode mode)
    {
        // CA1416 平台护栏走 inline 守卫（分析器不跨方法流转 SupportedOSPlatform 注解）
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    [Fact]
    public void TryDeleteDirectory_UnreadableChild_DeletesRestAndReturnsFalse()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Unix 权限模型：000 目录仅在 Linux 语义下确定性");
        }

        if (Environment.UserName == "root")
        {
            Assert.Skip("root 不受目录 000 权限约束，前提不成立");
        }

        var root = _temp.FilePath("tree");
        var locked = Path.Combine(root, "locked");
        Directory.CreateDirectory(root);
        var deletable = Path.Combine(root, "keep.txt");
        File.WriteAllText(deletable, "x");
        Directory.CreateDirectory(locked);
        var lockedFile = Path.Combine(locked, "x.bin");
        File.WriteAllText(lockedFile, "x");
        Chmod(locked, UnixFileMode.None);
        try
        {
            var result = FileUtilities.TryDeleteDirectory(root);

            // 可删部分照删（红落此断言：当前顶层枚举 UAE 裸穿）；不可读子目录留存
            // （000 目录 stat 不可达，Directory.Exists 恒 false——只能经父目录列表确认其名）；
            // root 因非空删不掉 → 返回 false（契约内的 best-effort 失败，不抛）
            Assert.False(File.Exists(deletable));
            Assert.Contains(locked, Directory.GetFileSystemEntries(root));
            Assert.False(result);
        }
        finally
        {
            Chmod(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void TryDeleteDirectory_UnreadableRoot_ReturnsFalseInsteadOfThrowing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Unix 权限模型：000 目录仅在 Linux 语义下确定性");
        }

        if (Environment.UserName == "root")
        {
            Assert.Skip("root 不受目录 000 权限约束，前提不成立");
        }

        var root = _temp.FilePath("locked-root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "x.txt"), "x");
        Chmod(root, UnixFileMode.None);
        try
        {
            var ex = Record.Exception(() => FileUtilities.TryDeleteDirectory(root));

            Assert.Null(ex); // 红落此断言：当前 UAE 裸穿
            Assert.False(FileUtilities.TryDeleteDirectory(root));
        }
        finally
        {
            Chmod(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void TryDeleteDirectory_DotfileInTree_DeletedCompletely()
    {
        // 复审 R1（F25 修复的回归审查，/tmp 探针实锤）：Linux 上 .NET 给点前缀条目标记
        // Hidden，EnumerationOptions 默认 AttributesToSkip=Hidden|System 会把 dotfile 从
        // 枚举里剔除——含 .hidden 的目录将删不净、Directory.Delete 失败。删除语义必须与
        // 旧无选项枚举一致（AttributesToSkip=None），点文件照删
        var root = _temp.FilePath("with-dot");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "keep.txt"), "x");
        File.WriteAllText(Path.Combine(root, ".yagl-marker"), "x");

        var result = FileUtilities.TryDeleteDirectory(root);

        Assert.True(result);
        Assert.False(Directory.Exists(root), "含点文件的目录必须删净（枚举不得跳过 Hidden）");
    }
}
