using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>
/// 多卷 zip 的条目（原始存储块）读取：中央目录在末卷，条目数据按本地头顺序排列在拼接空间里。
/// 关键坑：这类归档的中央目录尺寸字段可能不可信（终末地 v2 载荷把内层条目的尺寸写了上去），
/// 因此条目真实跨度 = 下一条目本地头偏移 − 本条目偏移（末条 = 中央目录偏移 − 本条目偏移），
/// 而不是中央目录声明的压缩尺寸。读取源是任意可寻址流：生产用
/// <see cref="SplitVolumeStream"/>（本地分卷文件），校验/测试可用远端 Range 支撑的流。
/// </summary>
public static class MultiPartZipEntryReader
{
    /// <summary>尾部窗口字节数（EOCD + ZIP64 记录/定位器都在其中）。</summary>
    private const int TailWindowBytes = ZipCentralDirectory.TailWindowBytes;

    /// <summary>中央目录读取上限（载荷形态的中央目录约百 KB 量级；超限放弃）。</summary>
    private const int MaxCentralDirectoryBytes = 8 * 1024 * 1024;

    /// <summary>
    /// 单条目体量上限：只为挡住坏中央目录/极端声明（否则可声明到卷集总长 9.36 GB）。真实载荷实测
    /// 最大条目 1199.9 MiB（2026-10-17：终末地 1.6.4 增量计划里有 5 个整文件条目 &gt;512 MiB，
    /// 最初定的 512 MiB 上限会把这些条目判成读不出、更新中途失败——用户实机 19:04 那次即此），
    /// 故取 <see cref="Array.MaxLength"/>（CLR 数组上限，2,147,483,591 字节；含 1.7× 余量）：
    /// 再大既不可分配（CLR 数组上限），也只会是坏声明——若官方将来出现大于该上限的合法块，本读取器
    /// 只能判"读不出"并由编排回退整包（不静默错）。大条目仍会在内存里成型（约 2 倍峰值），
    /// 流式化见记录里的缺口。
    /// </summary>
    private static readonly long MaxEntryBytes = Array.MaxLength;

    /// <summary>
    /// 生效中的单条目体量上限（测试缝优先）。取值本身有回归语义：2026-10-17 实机事故正是
    /// "常量取小了"（512 MiB &lt; 真实最大条目 1199.9 MiB），故由用例锚定下界（复审 P3-2）。
    /// </summary>
    internal static long EffectiveMaxEntryBytes => MaxEntryBytesForTests ?? MaxEntryBytes;

    /// <summary>测试缝：单条目体量上限（压小以验证超限路径）。设置它的测试必须与其它读取路径测试
    /// 同处 sequential 集合（Core.Tests 里的 SequentialTestCollection）——并行窗口会串扰（复审 P3-3）。</summary>
    internal static long? MaxEntryBytesForTests;

    /// <summary>
    /// 读取 <paramref name="stream"/> 所代表归档中 <paramref name="entryName"/> 的原始存储块
    /// （本地文件头 + 数据，按跨度界定）；找不到或形态不符返回 null 并记日志。
    /// </summary>
    public static byte[]? TryReadEntry(Stream stream, string entryName, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            if (!TryLocateEntry(stream, entryName, out var offset, out var span, logger))
            {
                return null;
            }

            var blob = ReadRange(stream, offset, (int)span);
            if (blob.Length < 30 || BinaryPrimitives.ReadUInt32LittleEndian(blob) != 0x04034b50)
            {
                logger?.LogWarning("Multi-part zip: entry {Entry} has no local file header.", entryName);
                return null;
            }

            return blob;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Multi-part zip entry read failed: {Entry}.", entryName);
            return null;
        }
    }

    /// <summary>
    /// 定位条目"本地头 + 数据"块的偏移与真实跨度（跨度 = 下一条目本地头偏移 − 本条目偏移，
    /// 末条 = 中央目录起点；中央目录声明的尺寸不可信）。<see cref="TryReadEntry"/> 与流式读取共用。
    /// </summary>
    internal static bool TryLocateEntry(
        Stream stream, string entryName, out long offset, out long span, ILogger? logger)
    {
        offset = 0;
        span = 0;
        var length = stream.Length;
        if (length <= 0 || !stream.CanSeek)
        {
            return false;
        }

        var (cdOffset, cdSize) = LocateCentralDirectory(stream, length, logger);
        if (cdOffset <= 0 || cdSize <= 0 || cdSize > MaxCentralDirectoryBytes || cdOffset + cdSize > length)
        {
            logger?.LogWarning("Multi-part zip: unusable central directory (offset {Offset}, size {Size}).", cdOffset, cdSize);
            return false;
        }

        var centralDirectory = ReadRange(stream, cdOffset, (int)cdSize);
        var entries = ZipCentralDirectory.Parse(centralDirectory);
        var entry = ZipCentralDirectory.Find(entries, entryName);
        if (entry is null || entry.LocalHeaderOffset > long.MaxValue)
        {
            logger?.LogDebug("Multi-part zip: entry {Entry} not found ({Count} entries).", entryName, entries.Count);
            return false;
        }

        // 真实跨度：下一条目本地头偏移（末条 = 中央目录起点）− 本条目偏移
        var nextOffset = cdOffset;
        foreach (var candidate in entries)
        {
            if (candidate.LocalHeaderOffset > entry.LocalHeaderOffset
                && candidate.LocalHeaderOffset < (ulong)nextOffset)
            {
                nextOffset = (long)candidate.LocalHeaderOffset;
            }
        }

        span = nextOffset - (long)entry.LocalHeaderOffset;
        if (span < 30 || span > EffectiveMaxEntryBytes)
        {
            logger?.LogWarning("Multi-part zip: entry {Entry} has an implausible span ({Span}).", entryName, span);
            return false;
        }

        offset = (long)entry.LocalHeaderOffset;
        return true;
    }

    /// <summary>
    /// 把条目内容流式写入 <paramref name="destination"/>（本地头之后的载荷；AES 条目经口令解密），
    /// 大条目不再在内存里成型（2026-10-18：终末地 1.6.4 实测 1.2 GiB 条目约 2.4 GiB 峰值）。
    /// 返回 false = 条目不可读/认证不符——**destination 可能已被写入部分内容**，调用方负责丢弃
    /// 半成品（生产路径写 *.yagl-new / *.yagl-delta 临时文件，失败即删）。
    /// </summary>
    public static bool TryCopyContentTo(
        Stream stream,
        string entryName,
        string? password,
        Stream destination,
        CancellationToken cancellationToken = default,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(destination);
        try
        {
            if (!TryLocateEntry(stream, entryName, out var offset, out var span, logger))
            {
                return false;
            }

            var header = ReadRange(stream, offset, 30);
            if (header.Length < 30)
            {
                return false;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18));
            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var dataOffset = offset + 30 + nameLength + extraLength;
            // 数据长度取自本地头（载荷片段形态没有可信中央目录可核）；csize 为 0 时吃到块末尾
            var dataLength = compressedSize > 0 ? compressedSize : span - 30 - nameLength - extraLength;
            if (dataLength <= 0 || dataOffset + dataLength > offset + span)
            {
                logger?.LogWarning("Multi-part zip: entry {Entry} has an implausible data range.", entryName);
                return false;
            }

            if ((flags & 0x1) == 0)
            {
                return CopyRange(stream, dataOffset, dataLength, destination, cancellationToken, logger);
            }

            var extra = ReadRange(stream, offset + 30 + nameLength, extraLength);
            return WinZipAesReader.TryCopyDecrypted(
                stream, dataOffset, dataLength, extra, uncompressedSize, password, destination,
                cancellationToken, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Multi-part zip streaming read failed: {Entry}.", entryName);
            return false;
        }
    }

    /// <summary>固定缓冲区把流的一段拷到目标（短读返回 false）；每块之间响应取消。</summary>
    internal static bool CopyRange(
        Stream source, long offset, long count, Stream destination, CancellationToken cancellationToken, ILogger? logger)
    {
        source.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var remaining = count;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var got = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (got <= 0)
            {
                logger?.LogWarning("Multi-part zip: short read ({Remaining} bytes missing).", remaining);
                return false;
            }

            destination.Write(buffer, 0, got);
            remaining -= got;
        }

        return true;
    }

    /// <summary>
    /// 读取条目内容（本地头之后的载荷；<paramref name="password"/> 非 null 且条目为 WinZip AES 时解密）：
    /// 终末地 v2 载荷的两类条目都由它归一——AES 条目（补丁/加密文件）与明文 stored 条目（整文件）。
    /// </summary>
    public static byte[]? TryReadContent(
        Stream stream, string entryName, string? password = null, ILogger? logger = null)
    {
        var blob = TryReadEntry(stream, entryName, logger);
        if (blob is null || blob.Length < 30)
        {
            // 短块（截断流等）不得进入头部字段读取：裸 AOORE 会穿出本族的"失败折 null"契约（复审 P3-4）
            return null;
        }

        var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(6));
        var encrypted = (flags & 0x1) != 0;
        if (encrypted)
        {
            if (string.IsNullOrEmpty(password))
            {
                logger?.LogWarning("Multi-part zip entry {Entry} is encrypted but no password was given.", entryName);
                return null;
            }

            return WinZipAesReader.TryReadEntry(blob, password, entryName, logger);
        }

        var nameLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(26));
        var extraLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(28));
        var dataOffset = 30 + nameLength + extraLength;
        return dataOffset <= blob.Length ? blob[dataOffset..] : null;
    }

    /// <summary>列出归档条目名（中央目录顺序）；读不到中央目录或 CD 解析不出条目返回 null
    /// （与"列举成功但为空"区分：调用方据此判载荷不可用）。</summary>
    public static IReadOnlyList<string>? TryListEntryNames(Stream stream, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            var (cdOffset, cdSize) = LocateCentralDirectory(stream, stream.Length, logger);
            if (cdOffset <= 0 || cdSize <= 0 || cdSize > MaxCentralDirectoryBytes)
            {
                return null; // 与"列举成功但为空"区分（复审 P3-5）：调用方据此判载荷不可用
            }

            var parsed = ZipCentralDirectory.Parse(ReadRange(stream, cdOffset, (int)cdSize));
            if (parsed.Count == 0)
            {
                // "cdSize > 0 却零条目"必是坏 CD（真真空 zip 的 cdSize==0 已被上一闸拒成 null）：
                // 不能当"没有静态条目"放行（dll/exe 不更新却判成功，复审第六轮 P3-2）；
                // 留日志——事故复盘时这是关键线索（复审第七轮 P3-1）
                logger?.LogWarning("Multi-part zip: central directory parsed to zero entries ({Size} bytes).", cdSize);
                return null;
            }

            return [.. parsed.Select(entry => entry.Name)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Multi-part zip entry listing failed.");
            return null;
        }
    }

    /// <summary>定位中央目录（经典 EOCD → ZIP64）：返回偏移与长度，失败返回 (0, 0)。</summary>
    private static (long Offset, int Size) LocateCentralDirectory(Stream stream, long length, ILogger? logger)
    {
        var windowLength = (int)Math.Min(TailWindowBytes, length);
        var tail = ReadRange(stream, length - windowLength, windowLength);
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) != 0x06054b50)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20));
            if (length - windowLength + index + 22 + commentLength != length)
            {
                continue;
            }

            var cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index + 12));
            var cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index + 16));
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 10));
            if (cdOffset != 0xFFFFFFFF && cdSize != 0xFFFFFFFF && entries != 0xFFFF)
            {
                return (cdOffset, (int)cdSize);
            }

            // ZIP64：定位器在经典 EOCD 之前，指向 ZIP64 EOCD 记录
            var locatorIndex = -1;
            for (var probe = index - 4; probe >= 0; probe--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(probe)) == 0x07064b50)
                {
                    locatorIndex = probe;
                    break;
                }
            }

            if (locatorIndex < 0 || locatorIndex + 20 > tail.Length)
            {
                logger?.LogWarning("Multi-part zip: ZIP64 locator missing.");
                return (0, 0);
            }

            var zip64Offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(locatorIndex + 8));
            if (zip64Offset < 0 || zip64Offset + 56 > length)
            {
                return (0, 0);
            }

            var zip64 = zip64Offset >= length - windowLength
                ? tail.AsSpan((int)(zip64Offset - (length - windowLength)))
                : ReadRange(stream, zip64Offset, 56);
            if (zip64.Length < 56 || BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064b50)
            {
                logger?.LogWarning("Multi-part zip: unreadable ZIP64 end-of-central-directory record.");
                return (0, 0);
            }

            var cdSize64 = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64[40..]);
            var cdOffset64 = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64[48..]);
            return cdSize64 is <= 0 or > MaxCentralDirectoryBytes ? (0, 0) : (cdOffset64, (int)cdSize64);
        }

        logger?.LogWarning("Multi-part zip: no end-of-central-directory record found.");
        return (0, 0);
    }

    /// <summary>从流里按绝对偏移读一段（流是分卷拼接视图，跨卷由流自身处理）。</summary>
    internal static byte[] ReadRange(Stream stream, long offset, int count)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var got = stream.Read(buffer, read, count - read);
            if (got == 0)
            {
                break;
            }

            read += got;
        }

        return read == count ? buffer : buffer[..read];
    }
}
