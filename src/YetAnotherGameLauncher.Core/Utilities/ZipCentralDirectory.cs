using System.Buffers.Binary;
using System.Text;

namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>
/// zip 中央目录解析（ZIP64 哨兵字段回填）：远端分段读取（<see cref="RemoteZipEntryReader"/>）、
/// AES 内层 zip 与终末地 v2 更新载荷读取共用同一实现（单一事实源：破损/漂移只需修一处）。
/// </summary>
internal static class ZipCentralDirectory
{
    /// <summary>
    /// 尾部窗口大小（找 EOCD 用）：经典 EOCD 最坏跨度 = 22 + 65,535 字节注释 = 65,557 字节，
    /// ZIP64 定位器 + ZIP64 EOCD 还要再多 ~76 字节（65,633）——64 KiB 会短 21 字节（复审 P3-8，
    /// 2026-10-17），故取 128 KiB。EOCD 查找、suffix Range 探针、AES 片段定位共用此常数。
    /// </summary>
    internal const int TailWindowBytes = 128 * 1024;

    /// <summary>单个中央目录条目（ZIP64 真值已回填）。</summary>
    internal sealed record Entry(
        string Name, ushort Flags, uint Method, ulong CompressedSize, ulong UncompressedSize, ulong LocalHeaderOffset);

    /// <summary>解析中央目录字节；遇到首个非条目签名即停（损坏即止，不多抛）。</summary>
    internal static List<Entry> Parse(ReadOnlySpan<byte> centralDirectory)
    {
        var entries = new List<Entry>();
        var index = 0;
        while (index + 46 <= centralDirectory.Length
               && BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory[index..]) == 0x02014b50)
        {
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory[(index + 8)..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory[(index + 10)..]);
            ulong compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory[(index + 20)..]);
            ulong uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory[(index + 24)..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory[(index + 28)..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory[(index + 30)..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory[(index + 32)..]);
            ulong localHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory[(index + 42)..]);
            if (index + 46 + nameLength > centralDirectory.Length)
            {
                return entries;
            }

            var name = Encoding.UTF8.GetString(centralDirectory.Slice(index + 46, nameLength));
            var extra = centralDirectory.Slice(
                index + 46 + nameLength, Math.Min(extraLength, centralDirectory.Length - (index + 46 + nameLength)));
            ReadZip64Extra(extra, ref uncompressedSize, ref compressedSize, ref localHeaderOffset);
            entries.Add(new Entry(name, flags, method, compressedSize, uncompressedSize, localHeaderOffset));
            index += 46 + nameLength + extraLength + commentLength;
        }

        return entries;
    }

    /// <summary>按名查条目（Ordinal 精确匹配）。</summary>
    internal static Entry? Find(List<Entry> entries, string name)
    {
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>ZIP64 扩展字段（0x0001）：只补当前为哨兵的字段，出现顺序 = 未压缩 / 压缩 / 偏移 / 磁盘。</summary>
    private static void ReadZip64Extra(
        ReadOnlySpan<byte> extra, ref ulong uncompressedSize, ref ulong compressedSize, ref ulong localHeaderOffset)
    {
        var position = 0;
        while (position + 4 <= extra.Length)
        {
            var headerId = BinaryPrimitives.ReadUInt16LittleEndian(extra[position..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(position + 2)..]);
            if (position + 4 + size > extra.Length)
            {
                return;
            }

            if (headerId == 0x0001)
            {
                var field = position + 4;
                if (uncompressedSize == 0xFFFFFFFF && field + 8 <= extra.Length)
                {
                    uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(extra[field..]);
                    field += 8;
                }

                if (compressedSize == 0xFFFFFFFF && field + 8 <= extra.Length)
                {
                    compressedSize = BinaryPrimitives.ReadUInt64LittleEndian(extra[field..]);
                    field += 8;
                }

                if (localHeaderOffset == 0xFFFFFFFF && field + 8 <= extra.Length)
                {
                    localHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(extra[field..]);
                }

                return;
            }

            position += 4 + size;
        }
    }
}
