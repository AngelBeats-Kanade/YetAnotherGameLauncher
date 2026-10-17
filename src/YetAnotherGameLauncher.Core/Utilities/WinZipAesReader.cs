using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>
/// WinZip AES（AE-2）条目读取：PBKDF2-HMAC-SHA1 派生密钥 → AES-CTR 解密 → HMAC-SHA1
/// 校验认证码 → 按真实压缩方法（0 stored / 8 deflate，取自 0x9901 扩展字段）解压。
/// .NET 的 ZipArchive 不支持 method 99（AE-x）条目，故自实现（终末地 v2 更新的载荷把每条
/// HDiffPatch 补丁包在 AES-256 内层 zip 里，口令 = 接口下发的 cd_key）。
/// 两种形态都支持：完整 zip（有中央目录，按名查条目）与"本地头 + 数据"的单条目片段
/// （载荷里的条目没有中央目录，尺寸取自本地头）。失败（形态不符/口令不符/校验不符）返回 null 并记日志。
/// </summary>
public static class WinZipAesReader
{
    /// <summary>WinZip AES 固定 KDF 迭代数（AE-1/AE-2 规格）。</summary>
    private const int Pbkdf2Iterations = 1000;

    /// <summary>AES 数据块：salt(16) + 口令校验值(2) + 密文 + 认证码(10)。</summary>
    private const int SaltLength = 16;
    private const int PasswordVerifierLength = 2;
    private const int AuthCodeLength = 10;

    /// <summary>
    /// 读取 <paramref name="blob"/> 中名为 <paramref name="entryName"/> 的 AES 加密条目；
    /// <paramref name="entryName"/> 为 null 时取中央目录首条目或偏移 0 处的单条目片段。
    /// </summary>
    public static byte[]? TryReadEntry(
        ReadOnlySpan<byte> blob, string password, string? entryName = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        try
        {
            var entryOffset = LocateEntry(blob, entryName);
            if (entryOffset < 0)
            {
                logger?.LogDebug("AES zip entry not found: {Entry}", entryName ?? "<first>");
                return null;
            }

            // 本地文件头：flags@6（bit0 = 加密）、method@8、csize@18、usize@22、nlen@26、elen@28
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(blob[(entryOffset + 6)..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(blob[(entryOffset + 8)..]);
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(blob[(entryOffset + 18)..]);
            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(blob[(entryOffset + 22)..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(blob[(entryOffset + 26)..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(blob[(entryOffset + 28)..]);
            var dataOffset = entryOffset + 30 + nameLength + extraLength;
            if ((flags & 0x1) == 0
                || !TryParseAesExtra(blob.Slice(entryOffset + 30 + nameLength, extraLength), out var realMethod, out var keyLength))
            {
                logger?.LogDebug("Entry is not WinZip AES (flags {Flags:#x}, method {Method}).", flags, method);
                return null;
            }

            // 数据长度取自本地头（载荷片段形态没有中央目录可核）；csize 为 0 时吃到片段末尾
            var dataLength = compressedSize > 0 ? (int)compressedSize : blob.Length - dataOffset;
            if (dataOffset + dataLength > blob.Length || dataLength < SaltLength + PasswordVerifierLength + AuthCodeLength)
            {
                logger?.LogWarning("AES payload is truncated or too small ({Length} bytes at {Offset}).", dataLength, dataOffset);
                return null;
            }

            var data = blob.Slice(dataOffset, dataLength);
            var ciphertext = data.Slice(
                SaltLength + PasswordVerifierLength,
                dataLength - SaltLength - PasswordVerifierLength - AuthCodeLength);
            var expectedAuthCode = data[^AuthCodeLength..];

            // 密钥：2*keyLength + 2 字节（加密密钥 + 认证密钥 + 口令校验值）
            var keyMaterial = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), data[..SaltLength], Pbkdf2Iterations, HashAlgorithmName.SHA1,
                keyLength * 2 + PasswordVerifierLength);
            var encryptionKey = keyMaterial.AsSpan(0, keyLength);
            var authKey = keyMaterial.AsSpan(keyLength, keyLength);
            if (!CryptographicOperations.FixedTimeEquals(
                    keyMaterial.AsSpan(keyLength * 2, PasswordVerifierLength), data.Slice(SaltLength, PasswordVerifierLength)))
            {
                logger?.LogWarning("WinZip AES password verification failed.");
                return null;
            }

            // 认证码 = HMAC-SHA1(认证密钥, 密文) 的前 10 字节（WinZip AES 规格）
            Span<byte> mac = stackalloc byte[20];
            HMACSHA1.HashData(authKey, ciphertext, mac);
            if (!CryptographicOperations.FixedTimeEquals(mac[..AuthCodeLength], expectedAuthCode))
            {
                logger?.LogWarning("WinZip AES authentication code mismatch (corrupted payload).");
                return null;
            }

            var plaintext = new byte[ciphertext.Length];
            AesCtrXor(encryptionKey, ciphertext, plaintext);
            var content = realMethod switch
            {
                0 => plaintext,
                8 => RawDeflate.TryInflate(
                    plaintext,
                    uncompressedSize > 0 ? Math.Min(uncompressedSize, (uint)Array.MaxLength) : Array.MaxLength),
                _ => null,
            };
            if (content is null || (uncompressedSize > 0 && content.Length != uncompressedSize))
            {
                logger?.LogWarning(
                    "WinZip AES entry could not be decompressed (method {Method}, {Size} bytes expected).",
                    realMethod, uncompressedSize);
                return null;
            }

            return content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "WinZip AES entry read failed.");
            return null;
        }
    }

    /// <summary>
    /// 定位条目：**片段语义优先**——载荷条目是"本地头 + 数据"的单条目块（无中央目录），
    /// 只要 blob 起点的本地头条目名匹配（或未指定名字）就认偏移 0。绝不能先全域搜 EOCD 签名：
    /// 密文是伪随机字节（AES-CTR 输出），9 GB 载荷里期望出现 ~2 次 <c>PK\x05\x06</c>，
    /// 命中即把随机字段当中央目录、条目被判"读不出"且无回退（review P1，2026-10-17）。
    /// 仅当片段语义不成立（给定了名字且与首条目名不符）时才回退"完整 zip + 锚定 EOCD"路径。
    /// </summary>
    private static int LocateEntry(ReadOnlySpan<byte> blob, string? entryName)
    {
        if (blob.Length < 30 || BinaryPrimitives.ReadUInt32LittleEndian(blob) != 0x04034b50)
        {
            return -1;
        }

        if (entryName is null || EntryNameMatches(blob, entryName))
        {
            return 0;
        }

        var endOfCentralDirectory = FindEndOfCentralDirectory(blob);
        if (endOfCentralDirectory < 0)
        {
            return -1;
        }

        var cdSize = BinaryPrimitives.ReadUInt32LittleEndian(blob[(endOfCentralDirectory + 12)..]);
        var cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(blob[(endOfCentralDirectory + 16)..]);
        if (cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF || cdOffset + cdSize > (uint)endOfCentralDirectory)
        {
            return -1; // 本读取器不服务 ZIP64 中央目录（载荷条目都在 4 GiB 以下）
        }

        var entries = ZipCentralDirectory.Parse(blob.Slice((int)cdOffset, (int)cdSize));
        var entry = ZipCentralDirectory.Find(entries, entryName);
        return entry is null || entry.LocalHeaderOffset > int.MaxValue ? -1 : (int)entry.LocalHeaderOffset;
    }

    /// <summary>偏移 0 的本地头条目名是否等于给定名字（片段语义判据）。</summary>
    private static bool EntryNameMatches(ReadOnlySpan<byte> blob, string entryName)
    {
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(blob[26..]);
        if (30 + nameLength > blob.Length)
        {
            return false;
        }

        return string.Equals(Encoding.UTF8.GetString(blob.Slice(30, nameLength)), entryName, StringComparison.Ordinal);
    }

    /// <summary>
    /// 在尾部窗口里找经典 EOCD（PK\x05\x06）并要求"记录 + 注释长度"正好落在 blob 末尾——
    /// 密文/注释里碰巧出现的签名不得当中央目录（与 RemoteZipEntryReader/MultiPartZipEntryReader 同口径）。
    /// </summary>
    private static int FindEndOfCentralDirectory(ReadOnlySpan<byte> blob)
    {
        var windowStart = Math.Max(0, blob.Length - ZipCentralDirectory.TailWindowBytes);
        for (var index = blob.Length - 22; index >= windowStart; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(blob[index..]) != 0x06054b50)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(blob[(index + 20)..]);
            if (index + 22 + commentLength == blob.Length)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>0x9901 扩展字段：版本(1=AE-1/2=AE-2) + "AE" + 强度(1/2/3) + 真实压缩方法。</summary>
    private static bool TryParseAesExtra(ReadOnlySpan<byte> extra, out ushort realMethod, out int keyLength)
    {
        realMethod = 0;
        keyLength = 0;
        var position = 0;
        while (position + 4 <= extra.Length)
        {
            var headerId = BinaryPrimitives.ReadUInt16LittleEndian(extra[position..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(position + 2)..]);
            if (position + 4 + size > extra.Length)
            {
                return false;
            }

            if (headerId == 0x9901 && size >= 7)
            {
                // 只实现 AE-2（认证码 10 字节，版本字节 = 2）；AE-1 的校验布局不同，遇到即拒绝
                // （安静返回 null），不假装支持
                if (BinaryPrimitives.ReadUInt16LittleEndian(extra[(position + 4)..]) != 2)
                {
                    return false;
                }

                var strength = extra[position + 8];
                keyLength = strength switch
                {
                    1 => 16, // AES-128
                    2 => 24, // AES-192
                    3 => 32, // AES-256
                    _ => 0,
                };
                if (keyLength == 0 || extra[position + 6] != (byte)'A' || extra[position + 7] != (byte)'E')
                {
                    return false;
                }

                realMethod = BinaryPrimitives.ReadUInt16LittleEndian(extra[(position + 9)..]);
                return true;
            }

            position += 4 + size;
        }

        return false;
    }

    /// <summary>AES-CTR：计数器为 16 字节小端、自 1 递增；密钥流块 = AES-ECB(计数器块)。</summary>
    private static void AesCtrXor(ReadOnlySpan<byte> key, ReadOnlySpan<byte> ciphertext, Span<byte> output)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        Span<byte> counter = stackalloc byte[16];
        Span<byte> keystream = stackalloc byte[16];
        for (var offset = 0; offset < ciphertext.Length; offset += 16)
        {
            counter.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(counter, (ulong)(offset / 16 + 1));
            aes.EncryptEcb(counter, keystream, PaddingMode.None);
            var block = Math.Min(16, ciphertext.Length - offset);
            for (var i = 0; i < block; i++)
            {
                output[offset + i] = (byte)(ciphertext[offset + i] ^ keystream[i]);
            }
        }
    }

}
