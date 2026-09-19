using Xunit;
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
