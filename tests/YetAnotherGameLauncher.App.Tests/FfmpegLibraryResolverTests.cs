using Xunit;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// FFmpeg 原生库准备器的纯文件系统逻辑（Phase 4c，2026-09-19，P4 排除文件可回收项）：
/// LocateLibraryDir 的库目录定位——递归扫描 + avcodec 文件名模式匹配 + 退化输入守卫。
/// 不触碰 FFmpeg 绑定（静态方法不经过一次性初始化），完全离线。
/// </summary>
public class FfmpegLibraryResolverTests : IDisposable
{
    private readonly TestSupport.TempDir _tempDir = new();

    [Fact]
    public void DefaultDownloadRoot_LivesUnderDataDirectory_NotConfigDirectory()
    {
        // 2026-09-22 路径策略：FFmpeg 原生库（可重建，体积大）归数据目录；
        // 叶子目录按 RID 分（win-x64/linux-x64），此处只钉父目录策略不镜像 RID 分支
        Assert.Equal(
            Path.Combine(AppPaths.DataDirectory, "ffmpeg"),
            Path.GetDirectoryName(FfmpegLibraryResolver.DefaultDownloadRoot));
        Assert.False(FfmpegLibraryResolver.DefaultDownloadRoot.StartsWith(
            AppPaths.ConfigDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    public void Dispose() => _tempDir.Dispose();

    [Theory]
    [InlineData("libavcodec.so.63")]
    [InlineData("avcodec-63.dll")]
    [InlineData("avcodec.so")]
    [InlineData("libavcodec.dll")]
    public void LocateLibraryDir_AvcodecFileVariants_FindsOwningDirectory(string fileName)
    {
        var libDir = _tempDir.FilePath("dl", "ffmpeg", "bin");
        Directory.CreateDirectory(libDir);
        File.WriteAllBytes(Path.Combine(libDir, fileName), [0x01]);

        Assert.Equal(libDir, FfmpegLibraryResolver.LocateLibraryDir(_tempDir.Path));
    }

    [Fact]
    public void LocateLibraryDir_NestedUnderRoot_FindsDeepestMatch()
    {
        // 下载包布局：根目录下多层 tar/zip 解包目录，库在深处
        var libDir = _tempDir.FilePath("dl", "ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0", "lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllBytes(Path.Combine(libDir, "libavcodec.so.63"), [0x01]);

        Assert.Equal(libDir, FfmpegLibraryResolver.LocateLibraryDir(_tempDir.Path));
    }

    [Fact]
    public void LocateLibraryDir_LookalikeNames_Ignored()
    {
        // 模式锚定（^...$）：libavcodec_foo.so / xavcodec.dll 不是 avcodec 主库，不得误定位
        var dir = _tempDir.FilePath("dl", "fake");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "libavcodec_foo.so"), [0x01]);
        File.WriteAllBytes(Path.Combine(dir, "xavcodec.dll"), [0x01]);
        File.WriteAllBytes(Path.Combine(dir, "avcodec2.so.63"), [0x01]);

        Assert.Null(FfmpegLibraryResolver.LocateLibraryDir(_tempDir.Path));
    }

    [Fact]
    public void LocateLibraryDir_MissingOrEmptyRoot_ReturnsNull()
    {
        Assert.Null(FfmpegLibraryResolver.LocateLibraryDir(null));
        Assert.Null(FfmpegLibraryResolver.LocateLibraryDir(_tempDir.FilePath("nonexistent")));
        Assert.Null(FfmpegLibraryResolver.LocateLibraryDir(_tempDir.FilePath("empty-dir")));
    }
}

public class FfmpegLibraryResolverArchiveSandboxTests : IDisposable
{
    private readonly TestSupport.TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public void ExtractArchive_TarEntryEscapingToSiblingDirectory_IsRejected()
    {
        // 回归（2026-09-20 复审）：沙箱前缀比较曾缺目录分隔符——targetDir=/data/ff 时
        // 条目 "../ffx/x" 解析到兄弟目录 /data/ffx 会被放过（纵深防御缺口）
        var archivePath = _tempDir.FilePath("evil.tar");
        File.WriteAllBytes(archivePath, BuildTarWithEntry("../targetx/evil.txt", "evil"u8.ToArray()));

        var targetDir = _tempDir.FilePath("target");
        Directory.CreateDirectory(targetDir);

        Assert.Throws<IOException>(
            () => FfmpegLibraryResolver.ExtractArchive(archivePath, targetDir));
        Assert.False(File.Exists(_tempDir.FilePath("targetx", "evil.txt")));
        Assert.False(File.Exists(Path.Combine(targetDir, "evil.txt")));
    }

    /// <summary>手工构造含单个常规文件条目的最小 ustar 归档（不依赖 SharpCompress 写 API）。</summary>
    private static byte[] BuildTarWithEntry(string entryName, ReadOnlySpan<byte> content)
    {
        var header = new byte[512];
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(entryName);
        Array.Copy(nameBytes, header, Math.Min(nameBytes.Length, 100));
        WriteOctal(header, 100, "644".PadLeft(7, '0'));                 // mode
        WriteOctal(header, 108, "0".PadLeft(7, '0'));                   // uid
        WriteOctal(header, 116, "0".PadLeft(7, '0'));                   // gid
        WriteOctal(header, 124, content.Length.ToString().PadLeft(11, '0')); // size
        WriteOctal(header, 136, "0".PadLeft(11, '0'));                  // mtime
        header[156] = (byte)'0';                                        // typeflag：常规文件
        Array.Copy(System.Text.Encoding.ASCII.GetBytes("ustar"), 0, header, 257, 5); // magic
        Array.Copy(System.Text.Encoding.ASCII.GetBytes("00"), 0, header, 263, 2);    // version

        // 校验和：chksum 字段（148..156）先按空格占位，对整个头部求和后写 8 位八进制（6 位 + NUL + 空格）
        for (var i = 148; i < 156; i++)
        {
            header[i] = 0x20;
        }

        var checksum = header.Sum(b => (long)b);
        Array.Copy(
            System.Text.Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 "),
            0, header, 148, 8);

        using var ms = new MemoryStream();
        ms.Write(header);
        ms.Write(content);
        var padding = (512 - content.Length % 512) % 512;
        ms.Write(new byte[padding]);
        ms.Write(new byte[1024]); // 结束零块
        return ms.ToArray();
    }

    private static void WriteOctal(byte[] header, int offset, string value)
    {
        Array.Copy(System.Text.Encoding.ASCII.GetBytes(value + "\0"), 0, header, offset, value.Length + 1);
    }
}
