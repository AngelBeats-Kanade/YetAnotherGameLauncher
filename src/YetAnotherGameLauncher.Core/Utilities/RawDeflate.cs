using System.IO.Compression;

namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>
/// raw deflate 解压（带输出上限）：zip 的 method 8 是裸 deflate，压缩体小、膨胀后可能到 GB 级
/// （压缩炸弹；声明尺寸可撒谎，本仓实测过 CD 尺寸不可信）。两处 Inflate（远端条目读取器、
/// WinZip AES 条目读取器）共用，超限即返回 null 交调用方折算（复审 P3-7，2026-10-17）。
/// </summary>
internal static class RawDeflate
{
    /// <summary>解压 <paramref name="data"/>；输出超过 <paramref name="maxOutputBytes"/> 或流损坏返回 null。</summary>
    internal static byte[]? TryInflate(byte[] data, long maxOutputBytes)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[64 * 1024];
            while (true)
            {
                var read = deflate.Read(chunk, 0, chunk.Length);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + read > maxOutputBytes)
                {
                    return null; // 超出声明/上限的膨胀：按不可信载荷处理
                }

                output.Write(chunk, 0, read);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }
}
