using System.IO.Compression;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>
/// FFmpeg 资产微型归档夹具（tar 条目 ffmpeg-n9.0/lib/libavcodec.so.63，内容 "fake-lib"）：
/// 测试侧无 xz 压缩器（SharpCompress 只读、System.Formats.Tar 不压缩），Linux 形态预生成后嵌入。
/// 供 FfmpegLibraryResolver 下载/首运链路测试构造"SHA256 校验可通过的真归档"（内容是假库字节，
/// 绑定必失败或落到系统回退——需要绑定成功的用例不得使用本夹具）。
/// </summary>
public static class TestFfmpegArchive
{
    /// <summary>Linux 侧微型 tar.xz 归档（条目 ffmpeg-n9.0/lib/libavcodec.so.63，内容 "fake-lib"）。</summary>
    private const string TarXzFixtureBase64 =
        "/Td6WFoAAATm1rRGBMCtAYBQIQEWAAAAAAAAADVExI3gJ/8ApV0AM2Gw4GZuYgT1HRA0i3CBI9gMjj4N/MOOKJTdoFNC+3wF9Y/Zme49V3kU1x47/7hEseBpKLUEfxnU8l/U8++CsxbQmN2hMLg6KeOnsA7h8Ge39BxwbvyEqD6+ND7P14RHjDfwSXt1scFQ/nbAO/RgWLcFLb8twimNbG40/n1N6C70hWdKXTbXSMJAA7239PH++KNbZOnWY8du5dlEZiQSVcbtYtYAAAAAADJzoQeA/WrvAAHJAYBQAACaKjdJscRn+wIAAAAABFla";

    /// <summary>构造与平台资产格式配套的微型归档：Windows（zip 资产）测试内现造 zip；
    /// Linux（tar.xz 资产）用预生成夹具（macOS 等平台无 BtbnAsset，使用方自测平台门）。</summary>
    public static (byte[] Archive, string EntryPath) Create()
    {
        const string entryPath = "ffmpeg-n9.0/lib/libavcodec.so.63";
        if (OperatingSystem.IsWindows())
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(entryPath);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("fake-lib");
            }

            return (ms.ToArray(), entryPath);
        }

        return (Convert.FromBase64String(TarXzFixtureBase64), entryPath);
    }
}
