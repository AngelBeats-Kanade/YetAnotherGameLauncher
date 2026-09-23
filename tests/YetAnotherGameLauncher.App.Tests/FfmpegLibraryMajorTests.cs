using Xunit;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// FFmpeg 9.0（FFmpeg.AutoGen 绑定）各库的系统文件主版本号必须逐库精确：avcodec 的 63 不能
/// 复用到其它库（avutil 实际是 61——2026-09-24 实测本机 BtbN LGPL shared 构建的 soname）。
/// 此前所有库一律探测 lib*.so.63 / *-63.dll：精确匹配系统上 avcodec 命中预检后，绑定首个调用
/// av_version_info（avutil）在 avutil.so.63 处必败，且系统分支短路掉下载兜底，状态锁死永久失败。
/// 纯字符串映射直测，不触碰 FFmpeg 绑定与 dlopen（可并行，不进 sequential）。
/// </summary>
public class FfmpegLibraryMajorTests
{
    [Theory]
    [InlineData("avutil", 61)]
    [InlineData("avcodec", 63)]
    [InlineData("avformat", 63)]
    [InlineData("avdevice", 63)]
    [InlineData("avfilter", 12)]
    [InlineData("swresample", 7)]
    [InlineData("swscale", 10)]
    public void LibraryFileMajor_MappedLibraries_MatchFfmpeg90Sonames(string libraryName, int expected)
    {
        Assert.Equal(expected, FfmpegLibraryResolver.LibraryFileMajor(libraryName));
    }

    [Fact]
    public void LibraryFileMajor_UnknownLibrary_FallsBackToAvcodecMajor()
    {
        // 未收录库名回退 avcodec 主版本（与旧行为一致，仅作最后兜底）
        Assert.Equal(63, FfmpegLibraryResolver.LibraryFileMajor("postproc-unknown"));
    }
}
