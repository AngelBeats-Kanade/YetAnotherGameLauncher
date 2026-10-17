using Xunit;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Utilities;

/// <summary>
/// 多卷 zip 载荷的条目读取（终末地 v2 更新载荷形态）：中央目录在末卷，条目原始块按
/// "下一条目偏移 − 本条目偏移"界定——这类归档的中央目录尺寸字段不可信（写的是内层条目尺寸，
/// 实测踩坑：按声明尺寸取会被截断，hpatchz 报 9/16）。
/// </summary>
[Collection("sequential")]
public class MultiPartZipEntryReaderTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    /// <summary>真实载荷形态的内层条目（首次条目：本地头 + 数据，无中央目录）。</summary>
    private static byte[] AesInnerBlob() =>
    [
        .. Convert.FromBase64String(
            "UEsDBDMAAQBjAAqAUV0AAAAAPQEAAAAEAAAKAAsAYmluYXJ5LmJpbgGZBwACAEFFAwgAW6qq+m4KDpWl/mDe3rty7I1WyytB0qhmm5FjKJ7hOyHeG9O6cYdxtQ1Woclt307FV3K61n1JLjBnCcfeMr36QSTFG6p0q6uoL2gJ832ArEOnVgmiFbW+6PRWHjnvZKq2LDEjDZy0g3OdQa0qp6qI73y3YzQ9L1EcktGlc0i+3h/JtKodTVK4pD0KhhDAe2r26fAhO571w35aYvbrZSRQSWNlohGv6Y2yvtGjN/BK9TMY0k3rJXf5CGCuG5apRL3Gl7m/xZeTy3SomYDTnwyN90OGPGkVOBLiwmAOE0Vvqwd7AuiZa93o01KBDhZz+q/UjWop6TLnmq/KuIuZeFJyXb4oh4YO+2wYbE0M7IzZuQ0jrITFUan+WScHGvDaFOTSLAZz3AI79shyK+qhKWupZVxh8EKKH0FokxsEIBNQSwMEMwABAGMACoBRXQAAAABVAAAAiBMAAA4ACwByZXBldGl0aXZlLnR4dAGZBwACAEFFAwgABe+lmOnDvgumuYobxD3nGcen972yscOOcXCeeHBFQLAEGYqXIt3EpUotTA+0yShqMT3t/hzk3naBKfCBvM8YD+C11EAF5KqxIAmFKLF2uVLP1uNCrFBLAwQzAAEAYwAKgFFdAAAAADQAAAAYAAAACAALAHRleHQudHh0AZkHAAIAQUUDAADBWefYyQiffClETE0+uDW7bDqQMSWjppl7nJG/cVGCN3m+sXXnaZGFXsSUayGDJF2lclH/UEsBAj8DMwABAGMACoBRXQAAAAA9AQAAAAQAAAoALwAAAAAAAAAggKSBAAAAAGJpbmFyeS5iaW4KACAAAAAAAAEAGACIthKNDV7dAQAAAAAAAAAAAAAAAAAAAAABmQcAAgBBRQMIAFBLAQI/AzMAAQBjAAqAUV0AAAAAVQAAAIgTAAAOAC8AAAAAAAAAIICkgXABAAByZXBldGl0aXZlLnR4dAoAIAAAAAAAAQAYAFm0Eo0NXt0BAAAAAAAAAAAAAAAAAAAAAAGZBwACAEFFAwgAUEsBAj8DMwABAGMACoBRXQAAAAA0AAAAGAAAAAgALwAAAAAAAAAggKSB/AEAAHRleHQudHh0CgAgAAAAAAABABgAkLMSjQ1e3QEAAAAAAAAAAAAAAAAAAAAAAZkHAAIAQUUDAABQSwUGAAAAAAMAAwA3AQAAYQIAAAAA")
            .Take(129 + 317),
    ];

    /// <summary>
    /// 造一个"尺寸撒谎"的多卷载荷（与真实载荷同形：每条目恰好一个本地头 + 数据，
    /// 中央目录字段复制内层尺寸——真实跨度只能由偏移差得出）。分卷切在条目中间覆盖跨卷读取。
    /// </summary>
    private string[] BuildLyingPayload(int volumeCount)
    {
        var aesFragment = AesInnerBlob(); // 终末地形态：AES 条目块本身就是"本地头 + AES 数据"片段
        var blobs = new List<(string Name, byte[] Blob, int ViralCompressed, int ViralUncompressed)>
        {
            ("static/dll.bin", PlainEntry("static/dll.bin", "static-file-content"u8.ToArray()), 7, 26),
            ("binary.bin", aesFragment, 7, 26),
            ("delete_files.txt", PlainEntry("delete_files.txt", "old/a.chk\nold/b.chk\n"u8.ToArray()), 7, 26),
        };

        using var buffer = new MemoryStream();
        var entries = new List<(string Name, long Offset, int ViralC, int ViralU)>();
        foreach (var (name, blob, viralC, viralU) in blobs)
        {
            entries.Add((name, buffer.Position, viralC, viralU));
            buffer.Write(blob);
        }

        var cdOffset = buffer.Length;
        foreach (var (name, offset, viralC, viralU) in entries)
        {
            WriteCentralDirectoryEntry(buffer, name, offset, viralC, viralU);
        }

        var cdSize = buffer.Length - cdOffset;
        WriteEndOfCentralDirectory(buffer, entries.Count, cdOffset, cdSize);
        var bytes = buffer.ToArray();

        var volumes = new string[volumeCount];
        var slice = (bytes.Length + volumeCount - 1) / volumeCount;
        for (var i = 0; i < volumeCount; i++)
        {
            var start = Math.Min(bytes.Length, i * slice);
            volumes[i] = _tempDir.FilePath($"payload.zip.{i + 1:D3}");
            File.WriteAllBytes(volumes[i], bytes[start..Math.Min(bytes.Length, start + slice)]);
        }

        return volumes;
    }

    /// <summary>明文 stored 条目块（本地头 + 内容），与真实载荷的整文件条目同形。</summary>
    private static byte[] PlainEntry(string name, byte[] content)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        using var buffer = new MemoryStream();
        Write(buffer, 0x04034b50u, 4);
        Write(buffer, 20, 2); // version needed
        Write(buffer, 0, 2); // flags
        Write(buffer, 0, 2); // method = stored
        Write(buffer, 0, 4); // time+date
        Write(buffer, 0, 4); // crc（本读取器不校验明文条目）
        Write(buffer, content.Length, 4);
        Write(buffer, content.Length, 4);
        Write(buffer, nameBytes.Length, 2);
        Write(buffer, 0, 2);
        buffer.Write(nameBytes);
        buffer.Write(content);
        return buffer.ToArray();
    }

    private static void WriteCentralDirectoryEntry(Stream buffer, string name, long offset, int csize, int usize)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        Write(buffer, 0x02014b50u, 4);
        Write(buffer, 20, 2);
        Write(buffer, 20, 2);
        Write(buffer, 0, 2);
        Write(buffer, 0, 2);
        Write(buffer, 0, 4);
        Write(buffer, 0, 4);
        Write(buffer, csize, 4); // 撒谎：写内层尺寸
        Write(buffer, usize, 4);
        Write(buffer, nameBytes.Length, 2);
        Write(buffer, 0, 2); // extra
        Write(buffer, 0, 2); // comment
        Write(buffer, 0, 2); // disk
        Write(buffer, 0, 2); // internal
        Write(buffer, 0, 4); // external
        Write(buffer, (int)offset, 4);
        buffer.Write(nameBytes);
    }

    private static void WriteEndOfCentralDirectory(Stream buffer, int count, long cdOffset, long cdSize)
    {
        Write(buffer, 0x06054b50u, 4);
        Write(buffer, 0, 2);
        Write(buffer, 0, 2);
        Write(buffer, count, 2);
        Write(buffer, count, 2);
        Write(buffer, (int)cdSize, 4);
        Write(buffer, (int)cdOffset, 4);
        Write(buffer, 0, 2);
    }

    private static void Write(Stream stream, uint value, int bytes) => Write(stream, (ulong)value, bytes);

    private static void Write(Stream stream, int value, int bytes) => Write(stream, (ulong)value, bytes);

    private static void Write(Stream stream, ulong value, int bytes)
    {
        for (var i = 0; i < bytes; i++)
        {
            stream.WriteByte((byte)(value >> (8 * i)));
        }
    }

    private static int FindCentralDirectory(byte[] bytes)
    {
        var eocd = bytes.AsSpan().LastIndexOf("PK\x05\x06"u8);
        return (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd + 16));
    }

    [Fact]
    public void TryReadEntry_LyingCentralDirectorySizes_ReturnsWholeBlob()
    {
        var volumes = BuildLyingPayload(volumeCount: 3);
        using var stream = new SplitVolumeStream(volumes);

        var blob = MultiPartZipEntryReader.TryReadEntry(stream, "binary.bin");

        Assert.NotNull(blob);
        // 必须按跨度取全（条目块本身就是 AES 片段），而不是按中央目录撒谎的 7 字节截断
        Assert.Equal(AesInnerBlob(), blob);
    }

    [Fact]
    public void TryReadEntry_EntriesAcrossVolumes_AllReadable()
    {
        var volumes = BuildLyingPayload(volumeCount: 5);
        using var stream = new SplitVolumeStream(volumes);

        var staticBlob = MultiPartZipEntryReader.TryReadEntry(stream, "static/dll.bin");
        var deleteList = MultiPartZipEntryReader.TryReadEntry(stream, "delete_files.txt");

        Assert.NotNull(staticBlob);
        Assert.Equal("static-file-content"u8.ToArray(), EntryData(staticBlob));
        Assert.NotNull(deleteList);
        Assert.Equal("old/a.chk\nold/b.chk\n"u8.ToArray(), EntryData(deleteList));
    }

    [Fact]
    public void TryReadEntry_Zip64PayloadWithLyingSizes_ReturnsWholeBlob()
    {
        // 真实载荷是 ZIP64（哨兵字段 + 扩展字段真值），且尺寸撒谎两者叠加
        var blob = AesInnerBlob();
        using var stream = new MemoryStream(Zip64Builder.BuildStored("chunk.chk_patch", blob, lyingSizes: true));

        var read = MultiPartZipEntryReader.TryReadEntry(stream, "chunk.chk_patch");

        Assert.NotNull(read);
        Assert.Equal(blob, EntryData(read));
    }

    [Fact]
    public void TryReadContent_AesAndPlainEntries_Normalized()
    {
        // 载荷两类条目归一：AES 条目（用 cd_key 解出真实内容）+ 明文 stored 条目（整文件）
        var volumes = BuildLyingPayload(volumeCount: 2);
        using var stream = new SplitVolumeStream(volumes);

        var staticContent = MultiPartZipEntryReader.TryReadContent(stream, "static/dll.bin");
        var deltaContent = MultiPartZipEntryReader.TryReadContent(stream, "binary.bin", password: "S3cret-Pa55!");

        Assert.NotNull(staticContent);
        Assert.Equal("static-file-content"u8.ToArray(), staticContent);
        Assert.NotNull(deltaContent);
        // AES 条目解出的是夹具首条目内容（binary.bin：确定性字节序）
        Assert.Equal(Enumerable.Range(0, 1024).Select(i => (byte)(i % 251)).ToArray(), deltaContent);
    }

    [Fact]
    public void TryReadContent_EncryptedWithoutPassword_ReturnsNull()
    {
        var volumes = BuildLyingPayload(volumeCount: 2);
        using var stream = new SplitVolumeStream(volumes);

        Assert.Null(MultiPartZipEntryReader.TryReadContent(stream, "binary.bin"));
    }

    /// <summary>取条目块的数据段（跳过本地头；不依赖中央目录的撒谎尺寸）。</summary>
    private static byte[] EntryData(byte[] blob)
    {
        var nameLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(26));
        var extraLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(28));
        return blob[(30 + nameLength + extraLength)..];
    }

    [Fact]
    public void TryReadEntry_EntryBeyondSizeCap_ReturnsNull()
    {
        // review P3-8c：坏中央目录不得诱发无上限分配（真实 1.6.4 计划实测最大条目 1199.9 MiB；
        // 事故复盘见 artifacts/bugs.md）
        MultiPartZipEntryReader.MaxEntryBytesForTests = 64;
        try
        {
            var volumes = BuildLyingPayload(volumeCount: 2);
            using var stream = new SplitVolumeStream(volumes);

            Assert.Null(MultiPartZipEntryReader.TryReadEntry(stream, "binary.bin")); // 446 字节 > 64
        }
        finally
        {
            MultiPartZipEntryReader.MaxEntryBytesForTests = null;
        }
    }

    [Fact]
    public void EffectiveMaxEntryBytes_IsAboveMeasuredVendorMaximum()
    {
        // review P3-2：2026-10-17 实机事故正是"常量取小了"（512 MiB < 真实最大条目 1199.9 MiB，
        // 更新中断在第 267 条）——取值本身要有守卫，否则把常量改回 512 MiB 四套件照样全绿
        Assert.True(
            MultiPartZipEntryReader.EffectiveMaxEntryBytes > 1_200L * 1024 * 1024,
            $"单条目上限过低：{MultiPartZipEntryReader.EffectiveMaxEntryBytes}");
    }

    [Fact]
    public void TryListEntryNames_UnreadableArchive_ReturnsNullInsteadOfEmpty()
    {
        // review P3-5：列举失败必须与"确实没有条目"区分——调用方（静态条目复制）据此判载荷不可用；
        // 若把失败当空表，dll/exe 不更新却会判成功（半更新无自愈）
        using var stream = new MemoryStream(new byte[4096]);

        Assert.Null(MultiPartZipEntryReader.TryListEntryNames(stream));
    }

    [Fact]
    public void TryListEntryNames_ReadableButZeroEntryCentralDirectory_ReturnsNull()
    {
        // 复审第六轮 P3-2："cdSize > 0 却零条目"必是坏 CD——不能当"没有静态条目"放行
        var volumes = BuildLyingPayload(volumeCount: 1); // 先造合法载荷
        var bytes = File.ReadAllBytes(volumes[0]);
        // 找到 CD 起点（EOCD 里记录的偏移），把首 4 字节签名改掉
        var eocd = bytes.AsSpan().LastIndexOf("PK\x05\x06"u8);
        var cdOffset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd + 16));
        bytes[cdOffset] = 0x00;
        File.WriteAllBytes(volumes[0], bytes);
        using var stream = new SplitVolumeStream(volumes);

        Assert.Null(MultiPartZipEntryReader.TryListEntryNames(stream));
    }

    [Fact]
    public void TryReadEntry_UnknownEntry_ReturnsNull()
    {
        var volumes = BuildLyingPayload(volumeCount: 2);
        using var stream = new SplitVolumeStream(volumes);

        Assert.Null(MultiPartZipEntryReader.TryReadEntry(stream, "missing.bin"));
    }

    [Fact]
    public void TryReadEntry_NotAZip_ReturnsNull()
    {
        using var stream = new MemoryStream(new byte[4096]);

        Assert.Null(MultiPartZipEntryReader.TryReadEntry(stream, "anything"));
    }
}
