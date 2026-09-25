using System.Diagnostics;
using System.Globalization;
using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// F34（artifacts/bugs.md）：B 帧流 demuxer EOF 时解码器重排缓冲仍持有尾帧——原实现直接
/// return false 释放 CodecContext，缓冲尾帧全部丢失（循环接缝提前 0.1-0.5s、分析尾窗取不到
/// 真实末帧）。修复 = 官方 NULL flush packet 收尾（ffmpeg doxygen："If the decoder still has
/// frames buffered, it will return them after sending a flush packet"）。
/// 真实解码路径离线测不到（假库字节进不了解码器）——机器前提显式 Skip：本机需有 ffmpeg/ffprobe
/// CLI（生成 B 帧夹具与期望帧数）与可绑定的配套 FFmpeg 库。
/// </summary>
[Collection("sequential")]
public class FfmpegDecodeDrainTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void DecodeAllFrames_AtEof_DrainsBufferedBFrames()
    {
        if (!OperatingSystem.IsLinux()
            || !File.Exists("/usr/bin/ffmpeg") || !File.Exists("/usr/bin/ffprobe"))
        {
            Assert.Skip("需要 ffmpeg/ffprobe CLI 生成 B 帧夹具与期望帧数（Linux）");
        }

        var resolver = new FfmpegLibraryResolver(new NetworkProxyManager());
        if (!resolver.EnsureReady(CancellationToken.None))
        {
            Assert.Skip("本机无可绑定的配套 FFmpeg 库（EnsureReady 失败）");
        }

        var clip = _temp.FilePath("bframes.avi");
        RunTool("/usr/bin/ffmpeg",
        [
            "-y", "-f", "lavfi", "-i", "testsrc=duration=0.4:size=64x64:rate=25",
            "-c:v", "mpeg4", "-bf", "2", "-q:v", "2", clip,
        ]);
        Assert.True(File.Exists(clip) && new FileInfo(clip).Length > 0, "夹具生成失败");

        var hasB = RunTool("/usr/bin/ffprobe",
        [
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=has_b_frames", "-of", "csv=p=0", clip,
        ]).Trim();
        if (hasB == "0")
        {
            Assert.Skip("编码器未产生 B 帧缓冲（has_b_frames=0），前提不成立");
        }

        var expected = int.Parse(
            RunTool("/usr/bin/ffprobe",
            [
                "-v", "error", "-count_frames", "-select_streams", "v:0",
                "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", clip,
            ]).Trim(), CultureInfo.InvariantCulture);

        var decoded = new FfmpegVideoBackdropPlayer(resolver).CountDecodedFramesForTests(clip);

        Assert.Equal(expected, decoded); // 红形态：解码帧数 < ffprobe 解码计数（缓冲尾帧丢失）
    }

    /// <summary>同步运行 CLI 并取 stdout（输出极小；按纪律先异步读再限时等待）。</summary>
    private static string RunTool(string exe, string[] args)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var read = process.StandardOutput.ReadToEndAsync();
        if (!read.Wait(30_000))
        {
            process.Kill();
            Assert.Fail("CLI 30s 未完成");
        }

        return read.Result;
    }
}
