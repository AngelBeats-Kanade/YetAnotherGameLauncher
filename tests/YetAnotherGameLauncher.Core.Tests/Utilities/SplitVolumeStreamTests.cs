using System.IO.Compression;
using Xunit;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Utilities;

/// <summary>
/// 多分卷归档拼接流（终末地 packs 的 <c>…zip.001/002/…</c> 形态的底层机制）：
/// 把物理分卷呈现为一个连续可寻址的流，供 ZipArchive 直接读取。
/// </summary>
public class SplitVolumeStreamTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    /// <summary>把每个分卷写成一个文件（文件名沿用 .001/.002… 形态），返回按序路径。</summary>
    private string[] WriteVolumes(params byte[][] volumes)
    {
        var paths = new string[volumes.Length];
        for (var i = 0; i < volumes.Length; i++)
        {
            paths[i] = _tempDir.FilePath($"archive.zip.{i + 1:D3}");
            File.WriteAllBytes(paths[i], volumes[i]);
        }

        return paths;
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public void Read_SingleByteVolumes_ReturnsConcatenationAcrossEveryBoundary()
    {
        // 最坏边界情形：每卷 1 字节，任何一次读取都跨卷
        var content = "0123456789abcdefghijklmnopqrstuvwxyz"u8.ToArray();
        using var stream = new SplitVolumeStream(WriteVolumes([.. content.Select(b => new[] { b })]));

        Assert.Equal(content.Length, stream.Length);
        Assert.Equal(content, ReadAll(stream));
    }

    [Fact]
    public void Seek_RepositionsAcrossVolumes_ForRandomAccessReads()
    {
        // ZipArchive 的随机访问语义：先定位到中央目录再读条目，位置可前后跳
        var paths = WriteVolumes("AAAA"u8.ToArray(), "BBBB"u8.ToArray(), "CCCC"u8.ToArray());
        using var stream = new SplitVolumeStream(paths);

        stream.Seek(10, SeekOrigin.Begin); // 第三卷内
        Assert.Equal("CC"u8.ToArray(), ReadExactly(stream, 2));
        stream.Seek(-3, SeekOrigin.Current); // 9 → "CCC"
        Assert.Equal("CCC"u8.ToArray(), ReadExactly(stream, 3));
        stream.Seek(-2, SeekOrigin.End); // 10 → "CC"
        Assert.Equal("CC"u8.ToArray(), ReadExactly(stream, 2));
        stream.Seek(1, SeekOrigin.Begin); // 跨卷读取 8 字节 = "AAABBBBC"
        Assert.Equal("AAABBBBC"u8.ToArray(), ReadExactly(stream, 8));
    }

    [Fact]
    public void Read_PastEnd_ReturnsZero()
    {
        using var stream = new SplitVolumeStream(WriteVolumes("AB"u8.ToArray(), "CD"u8.ToArray()));

        Assert.Equal("ABCD"u8.ToArray(), ReadAll(stream));
        Assert.Equal(0, stream.Read(new byte[1])); // EOF 语义与 FileStream 一致
    }

    [Fact]
    public void Position_NegativeOffset_ThrowsInsteadOfSilentlyClamping()
    {
        using var stream = new SplitVolumeStream(WriteVolumes("AB"u8.ToArray()));

        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
    }

    [Fact]
    public void ZipArchive_OverSplitVolumes_ReadsEntriesSpanningBoundaries()
    {
        // 真实形态：一个 zip 被切成多卷，ZipArchive 经拼接流完整读取（EOCD/中央目录在末卷）
        var zip = TestZip.Create(("game.exe", "MZ-stub-payload"), ("data/config.ini", "cfg=1"));
        var volumes = new[] { zip[..1], zip[1..17], zip[17..(zip.Length - 3)], zip[(zip.Length - 3)..] };

        using var stream = new SplitVolumeStream(WriteVolumes(volumes));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal("MZ-stub-payload", ReadEntry(archive, "game.exe"));
        Assert.Equal("cfg=1", ReadEntry(archive, "data/config.ini"));

        static string ReadEntry(ZipArchive archive, string path)
        {
            using var entry = archive.GetEntry(path)!.Open();
            using var reader = new StreamReader(entry);
            return reader.ReadToEnd();
        }
    }

    [Fact]
    public void Read_MissingVolume_ThrowsFileNotFound()
    {
        // 分卷缺失（暂存被删/清单不全）：构造时长度快照先暴露（生产路径由 ExtractSplitArchive
        // 的折算 try 收住），读取时按需打开再抛——两条路径都表现为 IOException 族
        var paths = WriteVolumes("AAAA"u8.ToArray(), "BBBB"u8.ToArray());
        Assert.Throws<FileNotFoundException>(() => new SplitVolumeStream([paths[0], paths[1] + ".gone"]));

        using var stream = new SplitVolumeStream(paths);
        File.Delete(paths[1]); // 构造快照之后、读取之前被外部移除
        stream.Seek(4, SeekOrigin.Begin);

        Assert.Throws<FileNotFoundException>(() => stream.Read(new byte[1]));
    }

    [Fact]
    public void ReadOnlyStream_WriteOperations_AreRejected()
    {
        // 只读契约：写路径与长度变更都必须显式拒绝（拼接流只服务归档读取）
        using var stream = new SplitVolumeStream(WriteVolumes("AB"u8.ToArray()));

        stream.Flush(); // 只读流无缓冲，flush 为无操作
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        Assert.False(stream.CanWrite);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var got = stream.Read(buffer, read, count - read);
            Assert.NotEqual(0, got);
            read += got;
        }

        return buffer;
    }
}
