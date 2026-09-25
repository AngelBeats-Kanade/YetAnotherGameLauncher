using Xunit;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Utilities;

/// <summary>
/// 文件工具：目录树尽力删除（Windows 占用/只读容错）与原子写入的只读目标覆盖。
/// </summary>
public class FileUtilitiesTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public void TryDeleteDirectory_NestedTree_RemovesEverything()
    {
        var deep = _tempDir.FilePath("tree", "sub", "deeper");
        Directory.CreateDirectory(deep);
        File.WriteAllText(_tempDir.FilePath("tree", "root.txt"), "x");
        File.WriteAllText(Path.Combine(deep, "deep.txt"), "x");

        Assert.True(FileUtilities.TryDeleteDirectory(_tempDir.FilePath("tree")));
        Assert.False(Directory.Exists(_tempDir.FilePath("tree")));
    }

    [Fact]
    public void TryDeleteDirectory_Missing_ReturnsTrue()
    {
        Assert.True(FileUtilities.TryDeleteDirectory(_tempDir.FilePath("no-such-dir")));
    }

    [Fact]
    public async Task TryDeleteDirectory_UndeletableChild_ToleratesAndReturnsFalse()
    {
        // 跨平台各自复现"单个文件删不掉"（Windows 上 Directory.Delete(recursive) 会整体抛异常）：
        // Windows 用 FileShare.None 句柄占用，Linux 用父目录去写权限
        var locked = _tempDir.FilePath("locked");
        var inner = Path.Combine(locked, "inner");
        Directory.CreateDirectory(inner);
        var keepPath = Path.Combine(inner, "keep.txt");
        File.WriteAllText(keepPath, "x");
        var siblingPath = _tempDir.FilePath("locked", "sibling.txt");
        File.WriteAllText(siblingPath, "s");

        if (OperatingSystem.IsWindows())
        {
            await using var handle = new FileStream(keepPath, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.False(FileUtilities.TryDeleteDirectory(locked));
        }
        else
        {
            // root/CAP_DAC_OVERRIDE 豁免 DAC：去写权限构造不出"删不掉"形态（同族前提探针）
            if (DacExemptionProbe.Exempt(_tempDir.Path))
            {
                Assert.Skip("当前进程可无视权限位（root/CAP_DAC_OVERRIDE），删不掉形态不成立");
            }

            File.SetUnixFileMode(inner, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                Assert.False(FileUtilities.TryDeleteDirectory(locked));
            }
            finally
            {
                // 还原写权限，让 TempDir 清理不卡在残留文件上
                File.SetUnixFileMode(inner, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Assert.True(Directory.Exists(locked)); // 删不掉的残留按"尽力删除"语义保留
        Assert.False(File.Exists(siblingPath)); // 删得掉的兄弟文件已清理
    }

    [Fact]
    public async Task WriteAtomicAsync_ReadOnlyTarget_Overwritten()
    {
        // Windows 上只读目标会让 Move 覆盖抛 UnauthorizedAccessException；写入前就地解除属性
        var path = _tempDir.FilePath("cfg.json");
        await File.WriteAllTextAsync(path, "old");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        await FileUtilities.WriteAtomicAsync(path, "new");

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void IsExecutableFile_Windows_ExistingFileIsExecutable()
    {
        // Windows CI 腿（2026-09-19）：Windows 语义 = 文件存在即可执行（是否真可运行由
        // CreateProcess 按扩展名/清单裁决，预检不做）——POSIX 腿另有执行位用例
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows 专属分支：POSIX 上走执行位判定（另有 Linux 用例覆盖）");
        }

        var path = _tempDir.FilePath("game.exe");
        File.WriteAllBytes(path, "MZ"u8.ToArray());

        Assert.True(FileUtilities.IsExecutableFile(path));
        Assert.False(FileUtilities.IsExecutableFile(_tempDir.FilePath("missing.exe")));
    }

    [Fact]
    public void LaunchLogFilePath_SameSecondLaunches_AreUnique()
    {
        // 回归（2026-09-20）：时间戳精确到秒，同秒内对同一游戏二次启动会互相覆盖日志
        var first = FileUtilities.LaunchLogFilePath(_tempDir.Path, "wuthering-waves");
        var second = FileUtilities.LaunchLogFilePath(_tempDir.Path, "wuthering-waves");

        Assert.NotEqual(first, second);
        Assert.Matches(@"^launch-wuthering-waves-\d{8}-\d{6}-[0-9a-f]{4}\.log$",
            Path.GetFileName(first));
    }
}
