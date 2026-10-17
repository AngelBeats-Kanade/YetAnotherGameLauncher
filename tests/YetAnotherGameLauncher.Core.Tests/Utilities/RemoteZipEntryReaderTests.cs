using System.IO.Compression;
using System.Text;
using Xunit;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Utilities;

/// <summary>
/// 远程 zip 条目读取（终末地官方补丁包形态：单文件约 49 GiB，而删除清单条目只有 7 KB）：
/// 必须只经 HTTP Range 取"尾部 → 中央目录 → 条目"这些字节，绝不整包下载。
/// </summary>
[Collection("sequential")]
public class RemoteZipEntryReaderTests : IDisposable
{
    private const string Url = "https://cdn.example.com/patches/patch_1_6_4_1_5_3.zip";

    private readonly StubHttpHandler _handler = new();

    private HttpClient Client => new(_handler);

    public void Dispose() => RemoteZipEntryReader.TailWindowBytesForTests = null;

    private static byte[] Zip(params (string Name, string Content)[] entries) => TestZip.Create(entries);

    [Fact]
    public async Task TryReadEntry_ReadsEntryFromRemoteZip()
    {
        _handler.Map(Url, Zip(("docs/readme.txt", "ignore me"), ("delete_files.txt", "a/b.chk\nc/d.chk\n")));

        var bytes = await RemoteZipEntryReader.TryReadEntryAsync(Client, Url, "delete_files.txt");

        Assert.NotNull(bytes);
        Assert.Equal("a/b.chk\nc/d.chk\n", Encoding.UTF8.GetString(bytes));
        // 契约：只发 Range 请求（整包 49 GiB 形态下无 Range 的整取不可接受）
        Assert.All(_handler.Requests, request => Assert.NotNull(request.Headers.Range));
    }

    [Fact]
    public async Task TryReadEntry_CentralDirectoryAndEntryBeyondTailWindow_FetchedByRange()
    {
        // 尾部窗口是"只取尾部"的优化边界；窗口放不小（条目增多）时中央目录与条目数据必须
        // 按显式 Range 分块取回。缝压到 256 字节强制走分块路径
        RemoteZipEntryReader.TailWindowBytesForTests = 256;
        _handler.Map(Url, Zip(("big.bin", new string('x', 300_000)), ("delete_files.txt", "tail/target.chk\n")));

        var bytes = await RemoteZipEntryReader.TryReadEntryAsync(Client, Url, "delete_files.txt");

        Assert.NotNull(bytes);
        Assert.Equal("tail/target.chk\n", Encoding.UTF8.GetString(bytes));
        Assert.All(_handler.Requests, request => Assert.NotNull(request.Headers.Range));
    }

    [Fact]
    public async Task TryReadEntry_TargetIsFirstEntry_LocalHeaderAtOffsetZero_Reads()
    {
        // 复审非阻塞 8 的回归面：首条目本地头就在偏移 0（bytes=0-N 形态）。桩以前对这个形态回
        // "200 + 截断体"，读取器把它当整包缓存后误判"数据越过归档末尾"返回 null——给后续补
        // 首条目覆盖的人埋假红。修好后按 206 + Content-Range 正常读回全部内容
        var (zip, payload) = ZipWithDeterministicPayload("delete_files.txt", 200_000, ("tail.bin", "tail"));
        _handler.Map(Url, zip);

        var bytes = await RemoteZipEntryReader.TryReadEntryAsync(Client, Url, "delete_files.txt");

        Assert.NotNull(bytes);
        Assert.Equal(payload, bytes);
    }

    [Fact]
    public async Task TryReadEntry_MisalignedRangeResponse_RefusedInsteadOfWrongBytes()
    {
        // 复审 nit：206 与请求区间错位时绝不能把错字节当目标数据（F64 同族防线，ReadAsync 对齐守卫）。
        // 前两次请求（尾部探测、本地头）照常对齐，第三次（条目数据）开始错位——无守卫时
        // "错一位、长度恰好被截到 csize"的数据会被当成条目内容返回（stored 条目长度校验也拦不住）
        var (zip, _) = ZipWithDeterministicPayload("delete_files.txt", 200_000, ("tail.bin", "tail"));
        _handler.Map(Url, zip);
        _handler.ForcedRangeStart = 1;
        _handler.Mismatched206AfterRequest = 2;
        _handler.Mismatched206FirstRequest = 1;

        var bytes = await RemoteZipEntryReader.TryReadEntryAsync(Client, Url, "delete_files.txt");

        Assert.Null(bytes);
    }

    /// <summary>带确定性"不可压缩"填充的 zip：首条目为 stored 的 <paramref name="firstPayloadSize"/> 字节。</summary>
    private static (byte[] Zip, byte[] Payload) ZipWithDeterministicPayload(
        string firstName, int firstPayloadSize, params (string Name, string Content)[] rest)
    {
        var payload = new byte[firstPayloadSize];
        var state = 12345u;
        for (var i = 0; i < payload.Length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            payload[i] = (byte)(state >> 24);
        }

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var stream = archive.CreateEntry(firstName, CompressionLevel.NoCompression).Open())
            {
                stream.Write(payload);
            }

            foreach (var (name, content) in rest)
            {
                using var stream = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return (buffer.ToArray(), payload);
    }

    [Fact]
    public async Task TryReadEntry_StoredEntry_Reads()
    {
        // 打包器可用 0（stored）压缩方法落条目：读取器两种方法都要支持
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("delete_files.txt", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write("stored/entry.chk\n"u8);
        }

        _handler.Map(Url, buffer.ToArray());

        var bytes = await RemoteZipEntryReader.TryReadEntryAsync(Client, Url, "delete_files.txt");

        Assert.NotNull(bytes);
        Assert.Equal("stored/entry.chk\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task TryReadEntry_Zip64ArchiveWithSentinelFields_Reads()
    {
        // 真实补丁包是 ZIP64；合成夹具做不出 >4GiB 归档，手搓哨兵形态钉死该分支
        _handler.Map(Url, Zip64Builder.BuildStored("delete_files.txt", "zip64/entry.chk\n"u8.ToArray()));

        var bytes = await RemoteZipEntryReader.TryReadEntryAsync(Client, Url, "delete_files.txt");

        Assert.NotNull(bytes);
        Assert.Equal("zip64/entry.chk\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task TryReadEntry_EntryAbsentOrUnknownUrl_ReturnsNull()
    {
        _handler.Map(Url, Zip(("delete_files.txt", "x\n")));

        Assert.Null(await RemoteZipEntryReader.TryReadEntryAsync(Client, Url, "missing.txt"));
        Assert.Null(await RemoteZipEntryReader.TryReadEntryAsync(Client, "https://cdn.example.com/other.zip", "delete_files.txt"));
    }

    [Fact]
    public async Task TryReadEntry_PermanentHttpError_ReturnsNull()
    {
        _handler.Map(Url, Zip(("delete_files.txt", "x\n")));
        _handler.FailWithStatusFirstN = 1;
        _handler.FailWithStatusCode = System.Net.HttpStatusCode.Forbidden;

        Assert.Null(await RemoteZipEntryReader.TryReadEntryAsync(Client, Url, "delete_files.txt"));
    }

    [Fact]
    public async Task TryReadEntry_ServerIgnoresRange_StillReadsFromWholeBody()
    {
        // 不支持 Range 的服务器回 200 + 全量：小归档仍可用（大归档找不到中央目录 → null）
        _handler.IgnoreRangeAndReturnFull = true;
        _handler.Map(Url, Zip(("delete_files.txt", "whole/body.chk\n")));

        var bytes = await RemoteZipEntryReader.TryReadEntryAsync(Client, Url, "delete_files.txt");

        Assert.NotNull(bytes);
        Assert.Equal("whole/body.chk\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task TryReadEntry_TransientNetworkFailures_RetriedThenSucceeds()
    {
        // 瞬态网络失败（连接重置）不应让清理静默失效：前两次请求抛异常，第三次起正常
        _handler.FailFirstN = 1;
        _handler.Map(Url, Zip(("delete_files.txt", "after/retry.chk\n")));

        var bytes = await RemoteZipEntryReader.TryReadEntryAsync(Client, Url, "delete_files.txt");

        Assert.NotNull(bytes);
        Assert.Equal("after/retry.chk\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task TryReadEntry_ChunkedBodyBeyondCap_RefusedWithoutBuffering()
    {
        // review P2-1（2026-10-17）：体量闸只认 Content-Length，chunked（无长度）的整包 200
        // 会在缓冲之后才被拒——现在改为带上限流式读体，超限即中止
        var handler = new ChunkedResponseHandler(totalBytes: 1 << 20);
        try
        {
            RemoteZipEntryReader.MaxResponseBodyBytesForTests = 4096;

            var bytes = await RemoteZipEntryReader.TryReadEntryAsync(new HttpClient(handler), Url, "delete_files.txt");

            Assert.Null(bytes);
            Assert.True(handler.EmittedBytes <= 4096 + (64 * 1024), $"只应读到上限附近，实际 {handler.EmittedBytes}");
        }
        finally
        {
            RemoteZipEntryReader.MaxResponseBodyBytesForTests = null;
        }
    }

    /// <summary>无 Content-Length 的响应（非可寻址流 → HttpClient 用 chunked）：按需持续吐字节。</summary>
    private sealed class ChunkedResponseHandler(int totalBytes) : HttpMessageHandler
    {
        public long EmittedBytes { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new EmittingStream(this, totalBytes));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }

        private sealed class EmittingStream(ChunkedResponseHandler owner, int totalBytes) : Stream
        {
            private int _written;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var want = Math.Min(Math.Min(count, 64 * 1024), totalBytes - _written);
                if (want <= 0)
                {
                    return 0;
                }

                Array.Clear(buffer, offset, want);
                _written += want;
                owner.EmittedBytes += want;
                return want;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task TryReadEntry_NotAZip_ReturnsNull()
    {
        _handler.Map(Url, new byte[8192]);

        Assert.Null(await RemoteZipEntryReader.TryReadEntryAsync(Client, Url, "delete_files.txt"));
    }
}
