using System.Formats.Tar;
using Xunit;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// <see cref="FfmpegLibraryResolver.ExtractArchive"/> 对 tar 符号链接条目的还原回归（2026-09-24 实锤）：
/// BtbN 的 tar 里短名 soname（libavcodec.so.63）是指向真实文件（.63.1.102）的**符号链接条目**，
/// 旧实现只跳过目录条目、把链接条目当普通文件 WriteEntryTo——落盘成 0 字节普通文件。
/// 本机下载目录 14 个短名全部 0 字节（readelf "Failed to read file's magic number" 实证），
/// 叠加依赖序缺陷后 avformat/avcodec 永解析不到（见 LibraryDependencyOrder 测试）。
/// 符号链接创建仅 Linux 断言（Windows 无 dev-mode 权限会假红），tar 构造走 System.Formats.Tar
/// （BCL 内置，可现造含 SymbolicLink 条目的真 tar，SharpCompress 只读不压缩）。
/// </summary>
public class FfmpegLibraryResolverExtractTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    /// <summary>造一个含"真实文件 + 同目录短名符号链接"两条目的 plain tar（模拟 BtbN lib/ 布局）。</summary>
    private string CreateTarWithSymlink(string linkEntry, string linkTarget, string realEntry, byte[] realPayload)
    {
        var tarPath = Path.Combine(_tempDir.Path, $"fixture-{Guid.NewGuid():N}.tar");
        using var stream = File.Create(tarPath);
        using var writer = new TarWriter(stream);
        var real = new PaxTarEntry(TarEntryType.RegularFile, realEntry)
        {
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        };
        real.DataStream = new MemoryStream(realPayload, writable: false);
        writer.WriteEntry(real);

        var link = new PaxTarEntry(TarEntryType.SymbolicLink, linkEntry)
        {
            LinkName = linkTarget,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        };
        writer.WriteEntry(link);
        return tarPath;
    }

    [Fact]
    public void ExtractArchive_TarSymlinkEntry_BecomesRealSymlinkNotZeroByteFile()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("符号链接断言仅 Linux（Windows 无 dev-mode 权限符号链接创建会假红）");
        }

        var payload = "fake-avcodec"u8.ToArray();
        var tarPath = CreateTarWithSymlink(
            linkEntry: "pkg/lib/libavcodec.so.63",
            linkTarget: "libavcodec.so.63.1.102",
            realEntry: "pkg/lib/libavcodec.so.63.1.102",
            realPayload: payload);

        FfmpegLibraryResolver.ExtractArchive(tarPath, _tempDir.Path);

        var linkPath = Path.Combine(_tempDir.Path, "pkg/lib/libavcodec.so.63");
        // 红证据（2026-09-24 实测）：现状落盘 0 字节普通文件，ResolveLinkTarget 返回 null
        var target = File.ResolveLinkTarget(linkPath, returnFinalTarget: false);
        Assert.NotNull(target);
        Assert.Equal("libavcodec.so.63.1.102", target!.Name);
        // 链接可解析到真实文件内容（短名 soname 是加载器实际要用的形态）
        Assert.Equal(payload, File.ReadAllBytes(linkPath));
    }

    [Fact]
    public void ExtractArchive_TarSymlinkEntry_OverwritesLegacyFlattenedZeroByteFile()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("符号链接断言仅 Linux（Windows 无 dev-mode 权限符号链接创建会假红）");
        }

        // 存量坏目录自愈路径：修复前落盘的 0 字节普通文件挡在路上，重解压必须替换成真链接
        var libDir = Path.Combine(_tempDir.Path, "pkg/lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllBytes(Path.Combine(libDir, "libavcodec.so.63"), "legacy-flattened"u8.ToArray());

        var tarPath = CreateTarWithSymlink(
            linkEntry: "pkg/lib/libavcodec.so.63",
            linkTarget: "libavcodec.so.63.1.102",
            realEntry: "pkg/lib/libavcodec.so.63.1.102",
            realPayload: "real"u8.ToArray());

        FfmpegLibraryResolver.ExtractArchive(tarPath, _tempDir.Path);

        var target = File.ResolveLinkTarget(Path.Combine(libDir, "libavcodec.so.63"), returnFinalTarget: false);
        Assert.NotNull(target);
        Assert.Equal("libavcodec.so.63.1.102", target!.Name);
    }

    [Theory]
    [InlineData("../evil.so")]        // 相对逃逸到父目录
    [InlineData("../../evil.so")]     // 多级逃逸
    [InlineData("/etc/passwd")]       // 绝对目标：链接本身在沙箱内但指向沙箱外
    public void ExtractArchive_TarSymlinkEntry_EscapingTarget_Rejected(string linkTarget)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("符号链接断言仅 Linux（Windows 无 dev-mode 权限符号链接创建会假红）");
        }

        var tarPath = CreateTarWithSymlink(
            linkEntry: "pkg/lib/libavcodec.so.63",
            linkTarget: linkTarget,
            realEntry: "pkg/lib/libavcodec.so.63.1.102",
            realPayload: "real"u8.ToArray());

        // 与普通条目的沙箱拒绝同一异常形态（IOException，EnsureReady 下载分支的过滤器接得住）
        Assert.Throws<IOException>(() =>
            FfmpegLibraryResolver.ExtractArchive(tarPath, _tempDir.Path));
    }
}
