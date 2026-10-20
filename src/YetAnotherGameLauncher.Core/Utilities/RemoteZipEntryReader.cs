using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>
/// 远程 zip 归档的单个条目读取（HTTP Range 分段取数）：只下载"尾部窗口 → 中央目录 → 目标条目"
/// 这几段字节，不下载整个归档——终末地官方补丁包单文件形态约 49 GiB，而其中的
/// <c>delete_files.txt</c> 条目只有 7 KB（真机实测全程 4 个 Range 请求 ≈ 24 KB）。
/// 瞬态网络失败（连接重置/5xx/408/429）线性退避重试；4xx 其余判定永久失败不重试。
/// 任何异常或形态不符都折算为 null 并记日志：调用方把 null 当"拿不到数据"，
/// 不得因此让更新失败（取消除外，取消必须传播）。
/// </summary>
public static class RemoteZipEntryReader
{
    /// <summary>尾部窗口缺省字节数（EOCD + ZIP64 记录/定位器 + 常规中央目录尾段都在其中）。</summary>
    private const int DefaultTailWindowBytes = ZipCentralDirectory.TailWindowBytes;

    /// <summary>中央目录分段上限（超限放弃：本读取器只服务小清单类条目，不做通用远程归档挂载）。</summary>
    private const int MaxCentralDirectoryBytes = 8 * 1024 * 1024;

    /// <summary>单条目解压后上限（清单类条目量级；防止畸形巨条目把内存打爆）。</summary>
    private const int MaxEntryBytes = 4 * 1024 * 1024;

    /// <summary>单次响应体上限：服务器忽略 Range 回 200 + 整包（数十 GiB）时绝不缓冲。</summary>
    private const int MaxResponseBodyBytes = 32 * 1024 * 1024;

    /// <summary>测试缝：响应体上限（压小以强制走超限路径）。</summary>
    internal static int? MaxResponseBodyBytesForTests;

    private const int MaxFetchAttempts = 3;
    private const int RetryDelayMilliseconds = 200;
    private const int LocalHeaderProbeBytes = 30 + 1024;

    /// <summary>测试缝：尾部窗口字节数（生产恒用缺省值；压小以强制走分块路径）。设置它的测试必须与
    /// 其它读取路径测试同处 sequential 集合（Core.Tests 里的 SequentialTestCollection），
    /// 并行窗口会串扰（复审 P3-3）。</summary>
    internal static int? TailWindowBytesForTests;

    /// <summary>
    /// 读取 <paramref name="url"/> 指向的 zip 归档中名为 <paramref name="entryName"/> 的条目内容；
    /// 取不到（网络/状态码/非 zip/条目缺失/压缩方法不支持）时返回 null。
    /// </summary>
    public static async Task<byte[]?> TryReadEntryAsync(
        HttpClient httpClient,
        string url,
        string entryName,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        try
        {
            var remote = await RemoteArchive.OpenAsync(
                httpClient, url, TailWindowBytesForTests ?? DefaultTailWindowBytes, logger, cancellationToken).ConfigureAwait(false);
            if (remote is null)
            {
                return null;
            }

            var (cdOffset, cdSize) = await remote.LocateCentralDirectoryAsync(cancellationToken).ConfigureAwait(false);
            if (cdOffset < 0 || cdSize <= 0 || cdSize > MaxCentralDirectoryBytes || cdOffset + cdSize > remote.Length)
            {
                logger?.LogWarning("Remote zip {Url}: unusable central directory (offset {Offset}, size {Size}).", url, cdOffset, cdSize);
                return null;
            }

            var cd = await remote.ReadAsync(cdOffset, cdSize, cancellationToken).ConfigureAwait(false);
            var entry = FindEntry(cd, entryName);
            if (entry is null)
            {
                logger?.LogDebug("Remote zip {Url}: entry {Entry} not found.", url, entryName);
                return null;
            }

            if (entry.CompressedSize > MaxEntryBytes || entry.UncompressedSize > MaxEntryBytes)
            {
                logger?.LogWarning("Remote zip {Url}: entry {Entry} too large ({Size} bytes).", url, entryName, entry.UncompressedSize);
                return null;
            }

            var headerBytes = (int)Math.Min(LocalHeaderProbeBytes, remote.Length - (long)entry.LocalHeaderOffset);
            if (entry.LocalHeaderOffset >= (ulong)remote.Length || headerBytes < 30)
            {
                logger?.LogWarning("Remote zip {Url}: entry {Entry} header offset is out of range.", url, entryName);
                return null;
            }

            var header = await remote.ReadAsync((long)entry.LocalHeaderOffset, headerBytes, cancellationToken).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034b50)
            {
                logger?.LogWarning("Remote zip {Url}: entry {Entry} has no readable local header.", url, entryName);
                return null;
            }

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var dataOffset = (long)entry.LocalHeaderOffset + 30 + nameLength + extraLength;
            if (dataOffset + (long)entry.CompressedSize > remote.Length)
            {
                logger?.LogWarning("Remote zip {Url}: entry {Entry} data extends past the archive end.", url, entryName);
                return null;
            }

            var data = await remote.ReadAsync(dataOffset, (int)entry.CompressedSize, cancellationToken).ConfigureAwait(false);
            var content = entry.Method switch
            {
                0 => data, // stored
                8 => RawDeflate.TryInflate(data, MetadataInflateCap), // deflate（带输出上限，复审 P3-7）
                _ => null,
            };
            if (content is null || (ulong)content.Length != entry.UncompressedSize)
            {
                logger?.LogWarning(
                    "Remote zip {Url}: entry {Entry} could not be read (method {Method}, {Size} bytes expected).",
                    url, entryName, entry.Method, entry.UncompressedSize);
                return null;
            }

            return content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 清理类数据是尽力而为：任何失败都折算 null（记日志保持可诊断），绝不拖垮更新
            logger?.LogWarning(ex, "Remote zip entry read failed: {Url} ({Entry}).", url, entryName);
            return null;
        }
    }

    /// <summary>本读取器只服务小元数据条目（delete_files.txt 实测 7.4 KB）：解压输出上限固定 64 MiB
    /// ——声明尺寸可撒谎，压缩炸弹不得把内存撑爆（复审 P3-7）。</summary>
    private const long MetadataInflateCap = 64 * 1024 * 1024;

    /// <summary>在中央目录里查条目（解析与 ZIP64 回填复用 ZipCentralDirectory，单一事实源）。</summary>
    private static CentralDirectoryEntry? FindEntry(ReadOnlySpan<byte> centralDirectory, string entryName)
    {
        var entry = ZipCentralDirectory.Find(ZipCentralDirectory.Parse(centralDirectory), entryName);
        return entry is null
            ? null
            : new CentralDirectoryEntry(entry.Method, entry.CompressedSize, entry.UncompressedSize, entry.LocalHeaderOffset);
    }

    /// <summary>zip 中央目录的条目（ZIP64 真值已回填）。</summary>
    private sealed record CentralDirectoryEntry(uint Method, ulong CompressedSize, ulong UncompressedSize, ulong LocalHeaderOffset);

    /// <summary>一次 Range 取数的结果（永久失败时由 FetchAsync 返回 null）。</summary>
    private sealed record RangeResponse(HttpStatusCode Status, byte[] Body, long? Total, long? RangeFrom);

    /// <summary>
    /// 远程归档的分段读取器：先取尾部窗口（含 EOCD），按需再经 Range 取中央目录与条目字节；
    /// 落在已取回字节里的读直接命中缓存，不重复下载。
    /// </summary>
    private sealed class RemoteArchive(HttpClient httpClient, string url, ILogger? logger)
    {
        private readonly HttpClient _httpClient = httpClient;
        private readonly string _url = url;
        private readonly ILogger? _logger = logger;
        private byte[] _tail = [];
        private long _tailStart;
        private long _length;
        private byte[]? _whole; // 服务器忽略 Range 时回 200 + 全量：整包落内存（仅小归档可用）

        /// <summary>归档总长度（Content-Range 总长；忽略 Range 的服务器用响应体长度）。</summary>
        public long Length => _length;

        /// <summary>取尾部窗口（suffix Range）；不支持 Range 的服务器回小全量时整包内存化。</summary>
        public static async Task<RemoteArchive?> OpenAsync(
            HttpClient httpClient, string url, int tailWindowBytes, ILogger? logger, CancellationToken cancellationToken)
        {
            var archive = new RemoteArchive(httpClient, url, logger);
            // bytes=-N（suffix）：一次请求同时拿到总长与尾部字节
            var probe = await archive.FetchAsync(new RangeHeaderValue(null, tailWindowBytes), cancellationToken).ConfigureAwait(false);
            if (probe is null)
            {
                logger?.LogWarning("Remote zip {Url}: tail probe failed (network or permanent status).", url);
                return null;
            }

            if (probe.Status == HttpStatusCode.OK)
            {
                // 服务器不支持/忽略 Range：响应体即整个归档（大归档下中央目录不可达而返回 null）
                if (probe.Body.Length == 0)
                {
                    return null;
                }

                logger?.LogDebug("Remote zip {Url}: server ignored Range; parsing {Size} bytes in memory.", url, probe.Body.Length);
                archive._tail = probe.Body;
                archive._tailStart = 0;
                archive._length = probe.Body.Length;
                archive._whole = probe.Body;
                return archive;
            }

            if (probe.Status != HttpStatusCode.PartialContent || probe.Total is null || probe.Body.Length == 0)
            {
                logger?.LogWarning("Remote zip {Url}: unexpected probe response {Status}.", url, probe.Status);
                return null;
            }

            archive._tail = probe.Body;
            archive._tailStart = probe.Total.Value - probe.Body.Length;
            archive._length = probe.Total.Value;
            return archive;
        }

        /// <summary>定位中央目录（经典 EOCD → ZIP64 EOCD；返回偏移与长度，失败返回 (-1, 0)）。</summary>
        public async Task<(long Offset, int Size)> LocateCentralDirectoryAsync(CancellationToken cancellationToken)
        {
            var eocd = FindEndOfCentralDirectory();
            if (eocd is not { } record)
            {
                _logger?.LogWarning("Remote zip {Url}: no end-of-central-directory record in the tail window.", _url);
                return (-1, 0);
            }

            // 经典 EOCD 字段：中央目录尺寸 @12、偏移 @16；哨兵（0xFFFFFFFF / 0xFFFF）时读 ZIP64 记录
            var needsZip64 = record.CentralDirectorySize == 0xFFFFFFFF
                             || record.CentralDirectoryOffset == 0xFFFFFFFF
                             || record.Entries == 0xFFFF;
            if (!needsZip64)
            {
                return (record.CentralDirectoryOffset, (int)record.CentralDirectorySize);
            }

            var locatorIndex = LastIndexOf(_tail, 0x07064b50, record.StartIndex); // PK\x06\x07
            if (locatorIndex < 0 || locatorIndex + 20 > _tail.Length)
            {
                _logger?.LogWarning("Remote zip {Url}: ZIP64 locator missing.", _url);
                return (-1, 0);
            }

            var zip64Offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(_tail.AsSpan(locatorIndex + 8));
            if (zip64Offset < 0 || zip64Offset + 56 > _length)
            {
                return (-1, 0);
            }

            var zip64 = ReadFromBuffered(zip64Offset, 56);
            if (zip64 is null)
            {
                zip64 = await FetchAsync(new RangeHeaderValue(zip64Offset, zip64Offset + 55), cancellationToken)
                    .ConfigureAwait(false) is { Status: HttpStatusCode.PartialContent } zip64Response
                    ? zip64Response.Body
                    : null;
            }

            if (zip64 is null
                || zip64.Length < 56
                || BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064b50
                || BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40)) > int.MaxValue)
            {
                _logger?.LogWarning("Remote zip {Url}: unreadable ZIP64 end-of-central-directory record.", _url);
                return (-1, 0);
            }

            var cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40));
            var cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48));
            return cdSize is <= 0 or > MaxCentralDirectoryBytes ? (-1, 0) : (cdOffset, (int)cdSize);
        }

        /// <summary>读取任意区间（命中已取回字节则零网络；否则一次 Range 请求）。</summary>
        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            if (count <= 0)
            {
                return [];
            }

            if (ReadFromBuffered(offset, count) is { } buffered)
            {
                return buffered;
            }

            var response = await FetchAsync(new RangeHeaderValue(offset, offset + count - 1), cancellationToken).ConfigureAwait(false)
                           ?? throw new IOException($"Range request failed for {_url} at {offset}.");
            if (response.Status == HttpStatusCode.OK)
            {
                // 中途改口忽略 Range：整包内存化后按偏移切片（大归档不缓冲，见 FetchAsync 的体量闸）
                _whole = response.Body;
                _tail = response.Body;
                _tailStart = 0;
                _length = response.Body.Length;
                return ReadFromBuffered(offset, count)
                       ?? throw new IOException($"Server ignored Range and the body does not cover {offset}..{offset + count}.");
            }

            // 对齐校验：错位 206 会把错字节当目标数据（下载器 F64 同款防线）
            if (response.Status != HttpStatusCode.PartialContent || response.RangeFrom != offset)
            {
                throw new IOException($"Range response misaligned: asked {offset}, got {response.RangeFrom?.ToString() ?? response.Status.ToString()}.");
            }

            return response.Body.Length <= count ? response.Body : response.Body[..count];
        }

        /// <summary>从已取回字节（全量缓存或尾部窗口）里切出区间；不在其中返回 null。</summary>
        private byte[]? ReadFromBuffered(long offset, int count)
        {
            if (offset < 0)
            {
                return null;
            }

            if (_whole is { } whole)
            {
                return offset + count <= whole.Length && offset <= int.MaxValue
                    ? whole.AsSpan((int)offset, count).ToArray()
                    : null;
            }

            if (offset < _tailStart || offset + count > _tailStart + _tail.Length)
            {
                return null;
            }

            return _tail.AsSpan((int)(offset - _tailStart), count).ToArray();
        }

        /// <summary>
        /// 单次 Range 取数（体量闸 + 瞬态重试）：4xx（408/429 除外）判定永久失败返回 null；
        /// 网络异常/5xx/408/429 线性退避重试至多 <see cref="MaxFetchAttempts"/> 次；
        /// 200 且响应体超过上限（服务器回整包）一律放弃——绝不把数十 GiB 缓冲进内存。
        /// </summary>
        private async Task<RangeResponse?> FetchAsync(RangeHeaderValue range, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                    request.Headers.Range = range;
                    using var response = await _httpClient.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || status >= 500;
                    if (status >= 400 && !transient)
                    {
                        _logger?.LogWarning("Remote zip {Url}: range {Range} failed with {Status}.", _url, range, status);
                        return null;
                    }

                    if (!transient)
                    {
                        var cap = MaxResponseBodyBytesForTests ?? MaxResponseBodyBytes;
                        if (response.Content.Headers.ContentLength > cap)
                        {
                            _logger?.LogWarning(
                                "Remote zip {Url}: range {Range} returned {Size} bytes (Range ignored); refusing to buffer it.",
                                _url, range, response.Content.Headers.ContentLength);
                            return null;
                        }

                        // 流式带上限读体：Chunked（无 Content-Length）的整包 200 同样不得进内存（review P2-1）
                        var body = await TryReadBodyAsync(response, cap, cancellationToken).ConfigureAwait(false);
                        if (body is null)
                        {
                            _logger?.LogWarning("Remote zip {Url}: range {Range} body exceeded the response cap.", _url, range);
                            return null;
                        }

                        return new RangeResponse(
                            response.StatusCode, body, response.Content.Headers.ContentRange?.Length, response.Content.Headers.ContentRange?.From);
                    }

                    _logger?.LogDebug("Remote zip {Url}: range {Range} got {Status} (attempt {Attempt}).", _url, range, status, attempt);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException
                                           || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    // 连接重置/读中断/HttpClient 超时（非调用方取消）：随下方退避重试
                    _logger?.LogDebug(ex, "Remote zip {Url}: range {Range} failed (attempt {Attempt}).", _url, range, attempt);
                }

                if (attempt >= MaxFetchAttempts)
                {
                    _logger?.LogWarning("Remote zip {Url}: range {Range} failed after {Attempts} attempts.", _url, range, MaxFetchAttempts);
                    return null;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(RetryDelayMilliseconds * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>带上限的流式读体：超过上限立即中止（不把整包缓冲进内存）。</summary>
        private static async Task<byte[]?> TryReadBodyAsync(
            HttpResponseMessage response, int cap, CancellationToken cancellationToken)
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > cap)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }

        /// <summary>尾部窗口里找 EOCD（PK\x05\x06）：自尾向头扫描并用"记录 + 注释长度 == 文件尾"校验。</summary>
        private EndOfCentralDirectory? FindEndOfCentralDirectory()
        {
            for (var index = _tail.Length - 22; index >= 0; index--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(_tail.AsSpan(index)) != 0x06054b50)
                {
                    continue;
                }

                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(_tail.AsSpan(index + 20));
                if (_tailStart + index + 22 + commentLength != _length)
                {
                    continue; // 注释里碰巧出现签名的假阳性：只认正好落在文件尾的那条记录
                }

                return new EndOfCentralDirectory(
                    index,
                    BinaryPrimitives.ReadUInt16LittleEndian(_tail.AsSpan(index + 10)),
                    BinaryPrimitives.ReadUInt32LittleEndian(_tail.AsSpan(index + 12)),
                    BinaryPrimitives.ReadUInt32LittleEndian(_tail.AsSpan(index + 16)));
            }

            return null;
        }

        private static int LastIndexOf(byte[] buffer, uint signature, int beforeIndex)
        {
            for (var index = Math.Min(beforeIndex, buffer.Length - 4); index >= 0; index--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(index)) == signature)
                {
                    return index;
                }
            }

            return -1;
        }
    }

    /// <summary>经典 EOCD 位置与中央目录字段（哨兵需后续按 ZIP64 解析）。</summary>
    private sealed record EndOfCentralDirectory(int StartIndex, ushort Entries, uint CentralDirectorySize, uint CentralDirectoryOffset);
}
