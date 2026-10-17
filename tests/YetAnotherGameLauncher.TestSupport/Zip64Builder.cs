namespace YetAnotherGameLauncher.TestSupport;

/// <summary>
/// 手搓 ZIP64 归档（stored 单条目）：中央目录的偏移/尺寸写 0xFFFFFFFF 哨兵、真值放 ZIP64
/// 扩展字段，尾部带 ZIP64 EOCD 记录 + 定位器。合成夹具无法做出 &gt;4GiB 的归档，
/// 用它钉哨兵解析分支（真实远程补丁包与 v2 载荷都是 ZIP64）。
/// </summary>
public static class Zip64Builder
{
    /// <summary>构造单条目 ZIP64 归档；<paramref name="lyingSizes"/> 为真时把中央目录的
    /// 尺寸改成小值（终末地 v2 载荷的"尺寸撒谎"形态：真实跨度只能由偏移差得出）。</summary>
    public static byte[] BuildStored(string name, byte[] content, bool lyingSizes = false)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var crc = Crc32(content);
        using var buffer = new MemoryStream();

        // 本地文件头（offset 0）
        var header = new List<byte>();
        Append(header, 0x04034b50u, 4); // PK\x03\x04
        Append(header, 45u, 2); // version needed（4.5 = ZIP64）
        Append(header, 0u, 2);
        Append(header, 0u, 2); // method = stored
        Append(header, 0u, 4); // time+date
        Append(header, crc, 4);
        Append(header, (uint)content.Length, 4); // 实体化（<4GiB）
        Append(header, (uint)content.Length, 4);
        Append(header, (uint)nameBytes.Length, 2);
        Append(header, 0u, 2); // extra length
        buffer.Write([.. header]);
        buffer.Write(nameBytes);
        buffer.Write(content);

        var cdOffset = buffer.Length;
        // ZIP64 扩展字段：只装哨兵字段（规格语义；撒谎形态只哨兵偏移 → 只有 8 字节）
        var extra = new byte[lyingSizes ? 4 + 8 : 4 + 24];
        Write(extra, 0x0001u, 2, 0);
        Write(extra, (ulong)(extra.Length - 4), 2, 2);
        if (lyingSizes)
        {
            Write(extra, 0ul, 8, 4);
        }
        else
        {
            Write(extra, (ulong)content.Length, 8, 4);
            Write(extra, (ulong)content.Length, 8, 12);
            Write(extra, 0ul, 8, 20);
        }

        Append(buffer, 0x02014b50u, 4); // PK\x01\x02
        Append(buffer, 45u, 2);
        Append(buffer, 45u, 2);
        Append(buffer, 0u, 2);
        Append(buffer, 0u, 2);
        Append(buffer, 0u, 4);
        Append(buffer, crc, 4);
        Append(buffer, lyingSizes ? 7u : 0xFFFFFFFFu, 4); // 压缩尺寸（哨兵或撒谎值）
        Append(buffer, lyingSizes ? 26u : 0xFFFFFFFFu, 4); // 未压缩尺寸
        Append(buffer, (uint)nameBytes.Length, 2);
        Append(buffer, (uint)extra.Length, 2);
        Append(buffer, 0u, 2);
        Append(buffer, 0u, 2);
        Append(buffer, 0u, 2);
        Append(buffer, 0u, 4);
        Append(buffer, 0xFFFFFFFFu, 4); // 本地头偏移（哨兵，真值在扩展字段）
        buffer.Write(nameBytes);
        buffer.Write(extra);

        var cdSize = buffer.Length - cdOffset;
        var zip64Offset = buffer.Length;
        Append(buffer, 0x06064b50u, 4); // PK\x06\x06
        Append(buffer, 44ul, 8);
        Append(buffer, 45u, 2);
        Append(buffer, 45u, 2);
        Append(buffer, 0u, 4);
        Append(buffer, 0u, 4);
        Append(buffer, 1ul, 8);
        Append(buffer, 1ul, 8);
        Append(buffer, (ulong)cdSize, 8);
        Append(buffer, (ulong)cdOffset, 8);
        Append(buffer, 0x07064b50u, 4); // PK\x06\x07 定位器
        Append(buffer, 0u, 4);
        Append(buffer, (ulong)zip64Offset, 8);
        Append(buffer, 1u, 4);
        Append(buffer, 0x06054b50u, 4); // PK\x05\x06（经典 EOCD，哨兵）
        Append(buffer, 0u, 2);
        Append(buffer, 0u, 2);
        Append(buffer, 0xFFFFu, 2);
        Append(buffer, 0xFFFFu, 2);
        Append(buffer, 0xFFFFFFFFu, 4);
        Append(buffer, 0xFFFFFFFFu, 4);
        Append(buffer, 0u, 2);
        return buffer.ToArray();
    }

    /// <summary>zip 的 CRC-32（本地头/中央目录都要填）。</summary>
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    private static void Append(Stream stream, ulong value, int bytes) => stream.Write([.. LittleEndian(value, bytes)]);

    private static void Append(List<byte> list, ulong value, int bytes) => list.AddRange(LittleEndian(value, bytes));

    private static void Write(byte[] buffer, ulong value, int bytes, int offset) =>
        LittleEndian(value, bytes).CopyTo(buffer, offset);

    private static byte[] LittleEndian(ulong value, int bytes)
    {
        var result = new byte[bytes];
        for (var i = 0; i < bytes; i++)
        {
            result[i] = (byte)(value >> (8 * i));
        }

        return result;
    }
}
