using System.Buffers.Binary;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>
/// 终末地 v2 增量载荷的测试夹具构造：真实载荷形态是"每条目恰好一个本地头 + 数据"的
/// zip，其**中央目录字段复制内层尺寸**（不可信——真实跨度只能由偏移差得出）。
/// AES 夹具由 7z 生成（`7z a -tzip -p'S3cret-Pa55!' -mem=AES256 fixture.zip text.txt repetitive.txt binary.bin`），
/// 覆盖 stored（text.txt）与 deflate（binary.bin / repetitive.txt）。
/// </summary>
public static class TestPayload
{
    /// <summary>AES 夹具口令。</summary>
    public const string AesPassword = "S3cret-Pa55!";

    /// <summary>7z 生成的完整 AES-256 zip 夹具（三条目：stored + deflate ×2）。</summary>
    public static byte[] AesFixtureZip => Convert.FromBase64String(
        "UEsDBDMAAQBjAAqAUV0AAAAAPQEAAAAEAAAKAAsAYmluYXJ5LmJpbgGZBwACAEFFAwgAW6qq+m4KDpWl/mDe3rty7I1WyytB0qhmm5FjKJ7hOyHeG9O6cYdxtQ1Woclt307FV3K61n1JLjBnCcfeMr36QSTFG6p0q6uoL2gJ832ArEOnVgmiFbW+6PRWHjnvZKq2LDEjDZy0g3OdQa0qp6qI73y3YzQ9L1EcktGlc0i+3h/JtKodTVK4pD0KhhDAe2r26fAhO571w35aYvbrZSRQSWNlohGv6Y2yvtGjN/BK9TMY0k3rJXf5CGCuG5apRL3Gl7m/xZeTy3SomYDTnwyN90OGPGkVOBLiwmAOE0Vvqwd7AuiZa93o01KBDhZz+q/UjWop6TLnmq/KuIuZeFJyXb4oh4YO+2wYbE0M7IzZuQ0jrITFUan+WScHGvDaFOTSLAZz3AI79shyK+qhKWupZVxh8EKKH0FokxsEIBNQSwMEMwABAGMACoBRXQAAAABVAAAAiBMAAA4ACwByZXBldGl0aXZlLnR4dAGZBwACAEFFAwgABe+lmOnDvgumuYobxD3nGcen972yscOOcXCeeHBFQLAEGYqXIt3EpUotTA+0yShqMT3t/hzk3naBKfCBvM8YD+C11EAF5KqxIAmFKLF2uVLP1uNCrFBLAwQzAAEAYwAKgFFdAAAAADQAAAAYAAAACAALAHRleHQudHh0AZkHAAIAQUUDAADBWefYyQiffClETE0+uDW7bDqQMSWjppl7nJG/cVGCN3m+sXXnaZGFXsSUayGDJF2lclH/UEsBAj8DMwABAGMACoBRXQAAAAA9AQAAAAQAAAoALwAAAAAAAAAggKSBAAAAAGJpbmFyeS5iaW4KACAAAAAAAAEAGACIthKNDV7dAQAAAAAAAAAAAAAAAAAAAAABmQcAAgBBRQMIAFBLAQI/AzMAAQBjAAqAUV0AAAAAVQAAAIgTAAAOAC8AAAAAAAAAIICkgXABAAByZXBldGl0aXZlLnR4dAoAIAAAAAAAAQAYAFm0Eo0NXt0BAAAAAAAAAAAAAAAAAAAAAAGZBwACAEFFAwgAUEsBAj8DMwABAGMACoBRXQAAAAA0AAAAGAAAAAgALwAAAAAAAAAggKSB/AEAAHRleHQudHh0CgAgAAAAAAABABgAkLMSjQ1e3QEAAAAAAAAAAAAAAAAAAAAAAZkHAAIAQUUDAABQSwUGAAAAAAMAAwA3AQAAYQIAAAAA");

    /// <summary>AES 夹具**首条目**的字节片段（本地头 + AES 数据，无中央目录）：终末地载荷条目同形。</summary>
    public static byte[] AesEntryFragment
    {
        get
        {
            var zip = AesFixtureZip;
            return zip.AsSpan(0, LocalHeaderLength(zip, 0) + (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(18))).ToArray();
        }
    }

    /// <summary>首条目（binary.bin）解出后的内容：确定性字节序。</summary>
    public static byte[] ExpectedAesFragmentContent => [.. Enumerable.Range(0, 1024).Select(i => (byte)(i % 251))];

    /// <summary>明文 stored 条目块（本地头 + 内容）：真实载荷的整文件条目同形。</summary>
    public static byte[] PlainEntry(string name, byte[] content)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        using var buffer = new MemoryStream();
        Write(buffer, 0x04034b50u, 4);
        Write(buffer, 20, 2); // version needed
        Write(buffer, 0, 2); // flags
        Write(buffer, 0, 2); // method = stored
        Write(buffer, 0, 4); // time+date
        Write(buffer, 0, 4); // crc（明文条目本读取器不校验）
        Write(buffer, content.Length, 4);
        Write(buffer, content.Length, 4);
        Write(buffer, nameBytes.Length, 2);
        Write(buffer, 0, 2); // extra
        buffer.Write(nameBytes);
        buffer.Write(content);
        return buffer.ToArray();
    }

    /// <summary>
    /// 拼一个载荷 zip：给定条目块按序排列，中央目录行复制各自本地头的字段（撒谎形态）。
    /// 返回整个载荷字节；分卷切片由调用方按需进行。
    /// </summary>
    public static byte[] BuildPayload(params byte[][] entryBlobs)
    {
        using var buffer = new MemoryStream();
        var entries = new List<(string Name, long Offset, uint Csize, uint Usize, ushort Flags, ushort Method)>();
        foreach (var blob in entryBlobs)
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(26));
            entries.Add((
                System.Text.Encoding.UTF8.GetString(blob.AsSpan(30, nameLength)),
                buffer.Position,
                BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(18)),
                BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(22)),
                BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(6)),
                BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(8))));
            buffer.Write(blob);
        }

        var cdOffset = buffer.Length;
        foreach (var (name, offset, csize, usize, flags, method) in entries)
        {
            WriteCentralDirectoryEntry(buffer, name, offset, csize, usize, flags, method);
        }

        var cdSize = buffer.Length - cdOffset;
        WriteEndOfCentralDirectory(buffer, entries.Count, cdOffset, cdSize);
        return buffer.ToArray();
    }

    private static int LocalHeaderLength(byte[] blob, int offset) =>
        30 + BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(offset + 26))
           + BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(offset + 28));

    private static void WriteCentralDirectoryEntry(
        Stream buffer, string name, long offset, uint csize, uint usize, ushort flags, ushort method)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        Write(buffer, 0x02014b50u, 4);
        Write(buffer, 20, 2);
        Write(buffer, 20, 2);
        Write(buffer, flags, 2);
        Write(buffer, method, 2);
        Write(buffer, 0, 4);
        Write(buffer, 0, 4);
        Write(buffer, csize, 4);
        Write(buffer, usize, 4);
        Write(buffer, nameBytes.Length, 2);
        Write(buffer, 0, 2);
        Write(buffer, 0, 2);
        Write(buffer, 0, 2);
        Write(buffer, 0, 2);
        Write(buffer, 0, 4);
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
}
