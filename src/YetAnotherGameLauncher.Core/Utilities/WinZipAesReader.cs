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
    internal const int SaltLength = 16;
    internal const int PasswordVerifierLength = 2;
    internal const int AuthCodeLength = 10;

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
    internal static bool TryParseAesExtra(ReadOnlySpan<byte> extra, out ushort realMethod, out int keyLength)
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
        using var decryptor = new AesCtrDecryptor(key.ToArray());
        decryptor.Decrypt(ciphertext, output);
    }

    /// <summary>
    /// 流式读取一个 AES 条目的内容并写入 <paramref name="destination"/>（2026-10-18：大条目
    /// 不再在内存里成型）。<paramref name="dataOffset"/>/<paramref name="dataLength"/> 界定
    /// "salt+口令校验值+密文+认证码"数据块（由 <c>MultiPartZipEntryReader</c> 按条目跨度算出）。
    /// 边解密边写、同步累计 HMAC；读尽后校验认证码与声明解压尺寸。返回 false = 形态/口令/认证
    /// 不符——**此时 destination 可能已有部分内容**，调用方负责丢弃半成品（生产路径写临时文件）。
    /// </summary>
    internal static bool TryCopyDecrypted(
        Stream source,
        long dataOffset,
        long dataLength,
        ReadOnlySpan<byte> extra,
        uint uncompressedSize,
        string? password,
        Stream destination,
        CancellationToken cancellationToken,
        ILogger? logger)
    {
        try
        {
            if (string.IsNullOrEmpty(password))
            {
                logger?.LogWarning("WinZip AES entry is encrypted but no password was given.");
                return false;
            }

            if (!TryParseAesExtra(extra, out var realMethod, out var keyLength)
                || dataLength < SaltLength + PasswordVerifierLength + AuthCodeLength)
            {
                logger?.LogDebug("Entry is not WinZip AES or its data block is truncated.");
                return false;
            }

            var prefix = MultiPartZipEntryReader.ReadRange(source, dataOffset, SaltLength + PasswordVerifierLength);
            if (prefix.Length < SaltLength + PasswordVerifierLength)
            {
                return false;
            }

            var keyMaterial = DeriveKeyMaterial(password, prefix.AsSpan(0, SaltLength), keyLength);
            var encryptionKey = keyMaterial.AsSpan(0, keyLength).ToArray();
            var authKey = keyMaterial.AsSpan(keyLength, keyLength).ToArray();
            if (!CryptographicOperations.FixedTimeEquals(
                    keyMaterial.AsSpan(keyLength * 2, PasswordVerifierLength),
                    prefix.AsSpan(SaltLength, PasswordVerifierLength)))
            {
                logger?.LogWarning("WinZip AES password verification failed.");
                return false;
            }

            var ciphertextOffset = dataOffset + SaltLength + PasswordVerifierLength;
            var ciphertextLength = dataLength - SaltLength - PasswordVerifierLength - AuthCodeLength;
            var expectedAuthCode = MultiPartZipEntryReader.ReadRange(
                source, dataOffset + dataLength - AuthCodeLength, AuthCodeLength);
            if (expectedAuthCode.Length < AuthCodeLength)
            {
                return false;
            }

            using var decrypting = new AesCtrDecryptingStream(
                source, ciphertextOffset, ciphertextLength, encryptionKey, authKey, expectedAuthCode, cancellationToken);
            long written;
            if (realMethod == 0)
            {
                written = CopyCounting(decrypting, destination, cancellationToken);
            }
            else if (realMethod == 8)
            {
                using var inflater = new System.IO.Compression.DeflateStream(
                    decrypting, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
                written = CopyCounting(inflater, destination, cancellationToken);
            }
            else
            {
                logger?.LogWarning("WinZip AES entry uses an unsupported compression method ({Method}).", realMethod);
                return false;
            }

            decrypting.Drain(); // 密文必须读尽：认证码覆盖全部密文（deflate 提前收尾时补齐剩余）
            if (!decrypting.IsAuthenticated)
            {
                logger?.LogWarning("WinZip AES authentication code mismatch (corrupted payload).");
                return false;
            }

            if (uncompressedSize > 0 && written != uncompressedSize)
            {
                logger?.LogWarning(
                    "WinZip AES entry size mismatch (expected {Expected}, wrote {Written}).", uncompressedSize, written);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "WinZip AES streaming read failed.");
            return false;
        }
    }

    /// <summary>PBKDF2-HMAC-SHA1 派生密钥材料（加密密钥 + 认证密钥 + 口令校验值）。</summary>
    internal static byte[] DeriveKeyMaterial(string password, ReadOnlySpan<byte> salt, int keyLength) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt.ToArray(), Pbkdf2Iterations, HashAlgorithmName.SHA1,
            keyLength * 2 + PasswordVerifierLength);

    /// <summary>固定缓冲区的流到流拷贝，返回写入字节数；每块之间响应取消。</summary>
    private static long CopyCounting(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long written = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var got = source.Read(buffer, 0, buffer.Length);
            if (got <= 0)
            {
                return written;
            }

            destination.Write(buffer, 0, got);
            written += got;
        }
    }

    /// <summary>
    /// AES-CTR 小端计数器解密器（跨调用连续）：同一密文流上多次 <see cref="Decrypt"/> 与
    /// 一次性解密整段等价（密钥流按 16 字节块推进）。
    /// </summary>
    internal sealed class AesCtrDecryptor : IDisposable
    {
        private readonly Aes _aes = Aes.Create();
        private readonly byte[] _counter = new byte[16];
        private readonly byte[] _keystream = new byte[16];
        private int _available; // 当前密钥流块剩余未用字节
        private ulong _blocks;

        public AesCtrDecryptor(byte[] key) => _aes.Key = key;

        /// <summary>解密一段（计数器块 = 块序号 + 1，小端 16 字节）。</summary>
        public void Decrypt(ReadOnlySpan<byte> input, Span<byte> output)
        {
            var consumed = 0;
            while (consumed < input.Length)
            {
                if (_available == 0)
                {
                    _counter.AsSpan().Clear();
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(_counter, _blocks + 1);
                    _aes.EncryptEcb(_counter, _keystream, PaddingMode.None);
                    _blocks++;
                    _available = 16;
                }

                var offsetInBlock = 16 - _available;
                var take = Math.Min(_available, input.Length - consumed);
                for (var i = 0; i < take; i++)
                {
                    output[consumed + i] = (byte)(input[consumed + i] ^ _keystream[offsetInBlock + i]);
                }

                consumed += take;
                _available -= take;
            }
        }

        public void Dispose() => _aes.Dispose();
    }

    /// <summary>
    /// "密文段 + AES-CTR 解密"的只读流（同步累计 HMAC-SHA1）；读尽后 <see cref="IsAuthenticated"/>
    /// 才可信（认证码是密文的函数，只能读到最后才能判）。
    /// </summary>
    private sealed class AesCtrDecryptingStream : Stream
    {
        private readonly Stream _source;
        private readonly long _offset;
        private readonly long _length;
        private readonly AesCtrDecryptor _decryptor;
        private readonly IncrementalHash _hmac;
        private readonly byte[] _expectedAuthCode;
        private readonly CancellationToken _cancellationToken;
        private long _position;
        private bool _verified;
        private bool _authenticated;

        public AesCtrDecryptingStream(
            Stream source,
            long offset,
            long length,
            byte[] encryptionKey,
            byte[] authKey,
            byte[] expectedAuthCode,
            CancellationToken cancellationToken)
        {
            _source = source;
            _offset = offset;
            _length = length;
            _decryptor = new AesCtrDecryptor(encryptionKey);
            _hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, authKey);
            _expectedAuthCode = expectedAuthCode;
            _cancellationToken = cancellationToken;
            _source.Seek(_offset, SeekOrigin.Begin);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        /// <summary>认证码校验结果（读尽后有效；未读尽时为 false）。</summary>
        public bool IsAuthenticated
        {
            get
            {
                Verify();
                return _authenticated;
            }
        }

        /// <summary>读尽剩余密文（deflate 提前收尾时补齐），使认证码校验生效。</summary>
        public void Drain()
        {
            Span<byte> scratch = stackalloc byte[4096];
            while (Read(scratch) > 0)
            {
                // 只丢弃，不解压
            }
        }

        public override int Read(Span<byte> buffer)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_position >= _length)
            {
                Verify();
                return 0;
            }

            var want = (int)Math.Min(buffer.Length, _length - _position);
            // 直接读进调用方缓冲区再原地解密（AES-CTR 是流密码：密文与明文等长）——
            // 不额外分配，也不让 64 KiB 级的块拷贝带出每次读取的临时数组
            var got = _source.Read(buffer[..want]);
            if (got <= 0)
            {
                Verify();
                return 0;
            }

            _hmac.AppendData(buffer[..got]);
            _decryptor.Decrypt(buffer[..got], buffer[..got]);
            _position += got;
            return got;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _decryptor.Dispose();
                _hmac.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Verify()
        {
            if (_verified)
            {
                return;
            }

            _verified = true;
            Span<byte> mac = stackalloc byte[20];
            _hmac.GetHashAndReset(mac);
            _authenticated = _position >= _length
                             && CryptographicOperations.FixedTimeEquals(
                                 mac[..AuthCodeLength], _expectedAuthCode);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
