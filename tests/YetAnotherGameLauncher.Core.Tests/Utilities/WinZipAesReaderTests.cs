using System.Security.Cryptography;
using System.Text;
using Xunit;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Utilities;

/// <summary>
/// WinZip AES（AE-2）条目读取：终末地 v2 更新的载荷把每条 HDiffPatch 补丁包在
/// AES-256 加密的内层 zip 里（口令 = 接口下发的 cd_key），必须在本进程内解出——
/// .NET 的 ZipArchive 不支持 method 99（AE-x），故自实现（PBKDF2-HMAC-SHA1 + AES-CTR + HMAC-SHA1 校验）。
/// 夹具由 7z 生成（`7z a -tzip -p'S3cret-Pa55!' -mem=AES256 fixture.zip text.txt repetitive.txt binary.bin`），
/// 覆盖 stored 与 deflate 两种真实压缩方法。
/// </summary>
public class WinZipAesReaderTests
{
    private const string Password = "S3cret-Pa55!";

    /// <summary>7z 生成的 AES-256 夹具（三条目：text.txt stored、repetitive.txt/binary.bin deflate）。</summary>
    private static byte[] Fixture => Convert.FromBase64String(
        "UEsDBDMAAQBjAAqAUV0AAAAAPQEAAAAEAAAKAAsAYmluYXJ5LmJpbgGZBwACAEFFAwgAW6qq+m4KDpWl/mDe3rty7I1WyytB0qhmm5FjKJ7hOyHeG9O6cYdxtQ1Woclt307FV3K61n1JLjBnCcfeMr36QSTFG6p0q6uoL2gJ832ArEOnVgmiFbW+6PRWHjnvZKq2LDEjDZy0g3OdQa0qp6qI73y3YzQ9L1EcktGlc0i+3h/JtKodTVK4pD0KhhDAe2r26fAhO571w35aYvbrZSRQSWNlohGv6Y2yvtGjN/BK9TMY0k3rJXf5CGCuG5apRL3Gl7m/xZeTy3SomYDTnwyN90OGPGkVOBLiwmAOE0Vvqwd7AuiZa93o01KBDhZz+q/UjWop6TLnmq/KuIuZeFJyXb4oh4YO+2wYbE0M7IzZuQ0jrITFUan+WScHGvDaFOTSLAZz3AI79shyK+qhKWupZVxh8EKKH0FokxsEIBNQSwMEMwABAGMACoBRXQAAAABVAAAAiBMAAA4ACwByZXBldGl0aXZlLnR4dAGZBwACAEFFAwgABe+lmOnDvgumuYobxD3nGcen972yscOOcXCeeHBFQLAEGYqXIt3EpUotTA+0yShqMT3t/hzk3naBKfCBvM8YD+C11EAF5KqxIAmFKLF2uVLP1uNCrFBLAwQzAAEAYwAKgFFdAAAAADQAAAAYAAAACAALAHRleHQudHh0AZkHAAIAQUUDAADBWefYyQiffClETE0+uDW7bDqQMSWjppl7nJG/cVGCN3m+sXXnaZGFXsSUayGDJF2lclH/UEsBAj8DMwABAGMACoBRXQAAAAA9AQAAAAQAAAoALwAAAAAAAAAggKSBAAAAAGJpbmFyeS5iaW4KACAAAAAAAAEAGACIthKNDV7dAQAAAAAAAAAAAAAAAAAAAAABmQcAAgBBRQMIAFBLAQI/AzMAAQBjAAqAUV0AAAAAVQAAAIgTAAAOAC8AAAAAAAAAIICkgXABAAByZXBldGl0aXZlLnR4dAoAIAAAAAAAAQAYAFm0Eo0NXt0BAAAAAAAAAAAAAAAAAAAAAAGZBwACAEFFAwgAUEsBAj8DMwABAGMACoBRXQAAAAA0AAAAGAAAAAgALwAAAAAAAAAggKSB/AEAAHRleHQudHh0CgAgAAAAAAABABgAkLMSjQ1e3QEAAAAAAAAAAAAAAAAAAAAAAZkHAAIAQUUDAABQSwUGAAAAAAMAAwA3AQAAYQIAAAAA");

    private static byte[] ExpectedBinary => [.. Enumerable.Range(0, 1024).Select(i => (byte)(i % 251))];

    private static string ExpectedRepetitive => string.Concat(Enumerable.Repeat("v2-delta-payload-fixture-", 200));

    [Fact]
    public void TryReadEntry_StoredEntry_ReturnsPlaintext()
    {
        var content = WinZipAesReader.TryReadEntry(Fixture, Password, "text.txt");

        Assert.NotNull(content);
        Assert.Equal("v2-delta-payload-fixture", Encoding.UTF8.GetString(content));
    }

    [Fact]
    public void TryReadEntry_DeflatedEntries_ReturnPlaintext()
    {
        var repetitive = WinZipAesReader.TryReadEntry(Fixture, Password, "repetitive.txt");
        var binary = WinZipAesReader.TryReadEntry(Fixture, Password, "binary.bin");

        Assert.NotNull(repetitive);
        Assert.Equal(ExpectedRepetitive, Encoding.UTF8.GetString(repetitive));
        Assert.NotNull(binary);
        Assert.Equal(ExpectedBinary, binary);
    }

    [Fact]
    public void TryReadEntry_EntryFragmentWithoutCentralDirectory_StillReads()
    {
        // 终末地载荷形态：条目是"本地头 + AES 数据"的片段（无中央目录，尺寸字段来自本地头）——
        // 取夹具首条目（binary.bin @0）的字节片段，按无中央目录形态读
        var fixture = Fixture;
        var firstEntryLength = LocalEntryLength(fixture, 0);
        var content = WinZipAesReader.TryReadEntry(fixture.AsSpan(0, firstEntryLength), Password);

        Assert.NotNull(content);
        Assert.Equal(ExpectedBinary, content);
    }

    [Fact]
    public void TryReadEntry_WrongPassword_ReturnsNull()
    {
        Assert.Null(WinZipAesReader.TryReadEntry(Fixture, "wrong-password", "text.txt"));
    }

    [Fact]
    public void TryReadEntry_CorruptedAuthCode_ReturnsNull()
    {
        // AES 认证码被改：HMAC 校验必须拦住（不能把第三方的坏数据当成功）
        var corrupted = (byte[])Fixture.Clone();
        var entryLength = LocalEntryLength(corrupted, 0);
        corrupted[entryLength - 1] ^= 0xFF;

        Assert.Null(WinZipAesReader.TryReadEntry(corrupted, Password, "binary.bin"));
    }

    [Fact]
    public void TryReadEntry_CorruptedCiphertext_ReturnsNull()
    {
        // 密文被改：HMAC 同样拦住
        var corrupted = (byte[])Fixture.Clone();
        var headerLength = LocalHeaderLength(corrupted, 0);
        corrupted[headerLength + 20] ^= 0xFF;

        Assert.Null(WinZipAesReader.TryReadEntry(corrupted, Password, "binary.bin"));
    }

    [Fact]
    public void TryReadEntry_NotAesEntry_ReturnsNull()
    {
        // 普通（未加密）zip 条目不是本读取器的服务对象
        Assert.Null(WinZipAesReader.TryReadEntry(TestZip.Create(("plain.txt", "hello")), Password, "plain.txt"));
    }

    [Fact]
    public void TryReadEntry_MissingEntryNameOrUnknownName_ReturnsNull()
    {
        Assert.Null(WinZipAesReader.TryReadEntry(Fixture, Password, "missing.txt"));
        Assert.Null(WinZipAesReader.TryReadEntry(Array.Empty<byte>(), Password, "text.txt"));
    }

    [Fact]
    public void TryReadEntry_CiphertextContainingEndOfCentralDirectorySignature_StillReads()
    {
        // review P1（2026-10-17）：载荷条目是"本地头 + 密文"片段，密文是伪随机字节——9 GB 载荷里
        // 期望出现 ~2 次 PK\x05\x06，若先全域搜签名就会被随机命中判"读不出"（无回退的永久失败）。
        // 本用例用测试侧自建加密器造一个"合法且密文含该签名"的条目，读取器必须照常读出
        var blob = BuildAesEntryWithSignatureInCiphertext("signature-in-ciphertext", atOffset: 8);

        // 载荷路径总是带条目名调用（TryReadContent 传外层名，实测与内层名逐字符一致），
        // 两种形态都必须读得出
        var named = WinZipAesReader.TryReadEntry(blob, Password, "entry.bin");
        var unnamed = WinZipAesReader.TryReadEntry(blob, Password);

        Assert.NotNull(named);
        Assert.NotNull(unnamed);
        Assert.Equal("signature-in-ciphertext".Length, named.Length); // 内容因覆盖而不同，长度与结构必须成立
    }

    [Fact]
    public void TryReadEntry_Ae1Entry_IsRejectedInsteadOfMisread()
    {
        // AE-1 的校验布局不同：只实现 AE-2 就必须拒绝（安静 null），不假装支持
        var blob = BuildAesEntryWithSignatureInCiphertext("ae1-entry", atOffset: 0, aeVersion: 1);

        Assert.Null(WinZipAesReader.TryReadEntry(blob, Password));
    }

    /// <summary>
    /// 测试侧 AES-256 加密器（独立实现，反向校验读取器）：stored 条目，密文在指定偏移被写入
    /// PK\x05\x06（按 CTR 对称性反解出对应明文，认证码按最终密文计算，故条目仍然合法）。
    /// </summary>
    private static byte[] BuildAesEntryWithSignatureInCiphertext(string plaintext, int atOffset, ushort aeVersion = 2)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(Password);
        var salt = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
        var keyMaterial = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, 1000, HashAlgorithmName.SHA1, 2 * 32 + 2);
        var encryptionKey = keyMaterial.AsSpan(0, 32);
        var authKey = keyMaterial.AsSpan(32, 32);
        var verifier = keyMaterial.AsSpan(64, 2).ToArray();

        var content = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[content.Length];
        using (var aes = Aes.Create())
        {
            aes.Key = encryptionKey.ToArray();
            Span<byte> counter = stackalloc byte[16];
            Span<byte> keystream = stackalloc byte[16];
            for (var offset = 0; offset < content.Length; offset += 16)
            {
                counter.Clear();
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(counter, (ulong)(offset / 16 + 1));
                aes.EncryptEcb(counter, keystream, PaddingMode.None);
                var block = Math.Min(16, content.Length - offset);
                for (var i = 0; i < block; i++)
                {
                    ciphertext[offset + i] = (byte)(content[offset + i] ^ keystream[i]);
                }
            }
        }

        "PK\x05\x06"u8.CopyTo(ciphertext.AsSpan(atOffset));
        var authCode = HMACSHA1.HashData(authKey, ciphertext)[..10];

        var aesData = new List<byte>();
        aesData.AddRange(salt);
        aesData.AddRange(verifier);
        aesData.AddRange(ciphertext);
        aesData.AddRange(authCode);

        var name = Encoding.UTF8.GetBytes("entry.bin");
        var extra = new byte[4 + 7];
        extra[0] = 0x01;
        extra[1] = 0x99;
        extra[2] = 7;
        extra[3] = 0;
        extra[4] = (byte)aeVersion; // AE 版本
        extra[5] = 0;
        extra[6] = (byte)'A';
        extra[7] = (byte)'E';
        extra[8] = 3; // AES-256
        extra[9] = 0; // 真实方法 = stored
        extra[10] = 0;

        using var buffer = new MemoryStream();
        Write(buffer, 0x04034b50u, 4);
        Write(buffer, 51, 2); // version needed（4.5 = AES）
        Write(buffer, 1, 2); // flags：加密
        Write(buffer, 99, 2); // method 99（vendor 与 7z 同款标记）
        Write(buffer, 0, 4); // time+date
        Write(buffer, 0, 4); // crc
        Write(buffer, aesData.Count, 4);
        Write(buffer, content.Length, 4);
        Write(buffer, name.Length, 2);
        Write(buffer, extra.Length, 2);
        buffer.Write(name);
        buffer.Write(extra);
        buffer.Write([.. aesData]);
        return buffer.ToArray();
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

    private static int LocalHeaderLength(byte[] blob, int offset) =>
        30 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(offset + 26))
           + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(offset + 28));

    private static int LocalEntryLength(byte[] blob, int offset) =>
        LocalHeaderLength(blob, offset)
        + (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(offset + 18));
}
