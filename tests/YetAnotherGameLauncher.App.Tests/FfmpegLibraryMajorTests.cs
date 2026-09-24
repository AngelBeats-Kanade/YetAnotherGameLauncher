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

    [Fact]
    public void LibraryDependencyOrder_SatisfiesBtbnRuntimeDeps()
    {
        // readelf 实测（2026-09-24，本机 BtbN n9.0 下载目录）：库间 DT_NEEDED 只有同伴 soname 且无
        // RUNPATH——glibc 不会到被加载库自己的目录找依赖，依赖必须先 dlopen 驻留。此测试钉住
        // 预载序必须满足的依赖约束（位置 = 预载序中的索引）：漏改/乱序会在此红，而不是在真机上
        // 以"avformat_open_input 抛 NotSupportedException stub"的形态回归（2026-09-24 P0 教训）
        var order = FfmpegLibraryResolver.LibraryDependencyOrder;
        int IndexOf(string name) => Array.IndexOf(order, name);

        Assert.Equal(7, order.Distinct().Count()); // 七库齐全且无重复
        // avutil 无同伴依赖，必须最先；swresample/swscale 只依赖 avutil
        Assert.True(IndexOf("avutil") < IndexOf("swresample"));
        Assert.True(IndexOf("avutil") < IndexOf("swscale"));
        // libavcodec NEEDED libswresample.so.7 + libavutil.so.61（readelf 实测）
        Assert.True(IndexOf("swresample") < IndexOf("avcodec"));
        Assert.True(IndexOf("avutil") < IndexOf("avcodec"));
        // libavformat NEEDED libavcodec.so.63 + libavutil.so.61（readelf 实测）
        Assert.True(IndexOf("avcodec") < IndexOf("avformat"));
        // avfilter/avdevice 在最末（依赖 avcodec/avformat/swscale 全家）
        Assert.True(IndexOf("avcodec") < IndexOf("avfilter"));
        Assert.True(IndexOf("avformat") < IndexOf("avdevice"));
    }
}
