using System.IO.Compression;
using System.Text;
using Xunit;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Tests.Utilities;

/// <summary>
/// 带输出上限的 raw deflate（复审 P3-7，2026-10-17）：zip 声明的未压缩尺寸可以撒谎，
/// 小压缩体膨胀到 GB 级不得把内存撑爆——超限即 null 交调用方折算。
/// </summary>
public class RawDeflateTests
{
    [Fact]
    public void TryInflate_NormalStream_RoundTrips()
    {
        var data = Encoding.UTF8.GetBytes("v2-delta-payload-fixture-" + new string('x', 5000));

        var result = RawDeflate.TryInflate(Compress(data), maxOutputBytes: 1 << 20);

        Assert.NotNull(result);
        Assert.Equal(data, result);
    }

    [Fact]
    public void TryInflate_ExpansionBeyondCap_ReturnsNullInsteadOfAllocating()
    {
        var bomb = Compress(new byte[4 * 1024 * 1024]);

        Assert.Null(RawDeflate.TryInflate(bomb, maxOutputBytes: 4096));
    }

    [Fact]
    public void TryInflate_OutputExactlyAtCap_Succeeds()
    {
        // 上限语义是 ">"（恰好等于上限时不判超限）——边界必须钉住，否则边界值会被误杀
        var data = Encoding.UTF8.GetBytes(new string('y', 2048));

        var result = RawDeflate.TryInflate(Compress(data), maxOutputBytes: data.Length);

        Assert.NotNull(result);
        Assert.Equal(data, result);
    }

    [Fact]
    public void TryInflate_CorruptStream_ReturnsNull()
    {
        // 首字节 0x6E('n') 的 BTYPE 位 = 3（保留值，规格上非法）→ 必抛 InvalidDataException
        var notDeflate = Encoding.ASCII.GetBytes("not-a-deflate-stream");
        Assert.Null(RawDeflate.TryInflate(notDeflate, maxOutputBytes: 1024));
    }

    private static byte[] Compress(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return buffer.ToArray();
    }
}
