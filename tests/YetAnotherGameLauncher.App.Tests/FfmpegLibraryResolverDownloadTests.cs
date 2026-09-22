using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// FFmpeg 首运下载链路的注入缝回归（2026-09-22）：下载 client 与落盘根目录必须可注入——
/// 此前 VideoBackdropPlayerCtsTests 用真实 resolver，在无系统库/无缓存的新环境会真实下载
/// （linux tar.xz 约 59MB / win zip 约 72MB，GitHub API 2026-09-22 实测）并写真实用户数据目录
/// （与 66b49fa"写真机目录"同族根因：缺注入缝）。直测 <c>DownloadAndExtract</c>
/// 的 checksums 拉取/SHA256 校验段，完全离线，不触碰 FFmpeg 绑定与 dlopen（可并行，不进 sequential）。
/// </summary>
public class FfmpegLibraryResolverDownloadTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public void CompositionRoot_DownloadClient_IsDedicatedSlowTimeout_NotContainerHijack()
    {
        // 回归（2026-09-22 独立审计）：容器注册过 HttpClient（全局 30 秒超时）后，类型激活会把
        // 可选参数 downloadClient 的 null 默认劫持为容器实例——15 分钟专用超时成死代码，
        // 慢链路首运下载 30s 即 TaskCanceled → 永久静态海报降级。可选参数断裂编译期不可见，
        // 必须经真实容器解析断言装配（先例：GameItemActionsTests 的组合根装配断言）
        using var sp = YetAnotherGameLauncher.App.BuildServices();
        var resolver = sp.GetRequiredService<FfmpegLibraryResolver>();

        var clientField = typeof(FfmpegLibraryResolver).GetField(
            "_downloadClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(clientField);
        var client = Assert.IsType<HttpClient>(clientField.GetValue(resolver));

        Assert.Equal(TimeSpan.FromMinutes(15), client.Timeout);
        Assert.NotSame(sp.GetRequiredService<HttpClient>(), client);
    }

    /// <summary>checksums 校验文件的完整下载地址（与生产同一事实源，不在测试里复制 URL）。</summary>
    private static string ChecksumsUrl => $"{FfmpegLibraryResolver.BtbnBaseUrl}/checksums.sha256";

    /// <summary>本平台资产的完整下载地址（同上，经 <c>BtbnAsset</c> 取名）。</summary>
    private static string AssetUrl =>
        $"{FfmpegLibraryResolver.BtbnBaseUrl}/{FfmpegLibraryResolver.BtbnAsset!.Value.Asset}";

    /// <summary>构造走注入 client 与注入根目录的 resolver（生产路径的缝即此两参数）。</summary>
    private static FfmpegLibraryResolver CreateResolver(StubHttpHandler stub, string root) => new(
        new NetworkProxyManager(),
        downloadClient: new HttpClient(stub),
        downloadRoot: root);

    [Fact]
    public async Task ChecksumFetchFailure_Throws_AndUsesOnlyInjectedClient()
    {
        // 新环境冷启动：checksums 拉不到（未映射 → 404）即中止。所有 HTTP 必须走注入的 stub——
        // 请求清单就是"零真网"的构造性证明（stub 是该 client 唯一的网络出口）
        var stub = new StubHttpHandler();
        var root = _tempDir.FilePath("root-fetch-fail");
        var resolver = CreateResolver(stub, root);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => resolver.DownloadAndExtract(CancellationToken.None));

        // 落盘必须走注入根目录：CreateDirectory 先于任何网络请求——"忽略注入根、落回真实数据目录
        // 默认根"的变异在此即红（临时根不会被创建），否则该缝可被静默回退而全套测试无报警
        Assert.True(Directory.Exists(root));
        var requested = Assert.Single(stub.Requests).RequestUri!.ToString();
        Assert.Equal(ChecksumsUrl, requested);
    }

    [Fact]
    public async Task AssetChecksumMismatch_Throws_AndExtractsNothing()
    {
        // 宁可不装也不装来路不明的库：资产字节与 checksums 宣告的 SHA256 不符 → 拒绝解压
        var stub = new StubHttpHandler();
        var asset = FfmpegLibraryResolver.BtbnAsset!.Value.Asset;
        var declared = Convert.ToHexString(SHA256.HashData("different bytes"u8.ToArray())).ToLowerInvariant();
        stub.Map(ChecksumsUrl, $"{declared}  *{asset}\n");
        stub.Map(AssetUrl, "corrupt asset bytes"u8.ToArray());
        var root = _tempDir.FilePath("root-mismatch");
        var resolver = CreateResolver(stub, root);

        await Assert.ThrowsAsync<CryptographicException>(
            () => resolver.DownloadAndExtract(CancellationToken.None));

        // 校验失败必须发生在解压前：根目录不得出现任何可定位的库文件
        Assert.Null(FfmpegLibraryResolver.LocateLibraryDir(root));
        Assert.Equal(2, stub.Requests.Count); // checksums + 资产各一次，无额外重试外溢
    }

    [Fact]
    public async Task ChecksumsMissingEntry_ThrowsInvalidData()
    {
        // checksums 文件存在但没有本平台资产的条目 → 拒绝安装（InvalidDataException），不碰资产下载
        var stub = new StubHttpHandler();
        stub.Map(ChecksumsUrl, "0000000000000000000000000000000000000000000000000000000000000000  other-asset.zip\n");
        var resolver = CreateResolver(stub, _tempDir.FilePath("root-no-entry"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => resolver.DownloadAndExtract(CancellationToken.None));

        Assert.Single(stub.Requests);
    }

    /// <summary>Linux 侧微型 tar.xz 归档夹具（tar 条目 ffmpeg-n9.0/lib/libavcodec.so.63，内容 "fake-lib"）：
    /// 测试侧无 xz 压缩器（SharpCompress 只读、System.Formats.Tar 不压缩），预生成后嵌入。</summary>
    private const string TarXzFixtureBase64 =
        "/Td6WFoAAATm1rRGBMCtAYBQIQEWAAAAAAAAADVExI3gJ/8ApV0AM2Gw4GZuYgT1HRA0i3CBI9gMjj4N/MOOKJTdoFNC+3wF9Y/Zme49V3kU1x47/7hEseBpKLUEfxnU8l/U8++CsxbQmN2hMLg6KeOnsA7h8Ge39BxwbvyEqD6+ND7P14RHjDfwSXt1scFQ/nbAO/RgWLcFLb8twimNbG40/n1N6C70hWdKXTbXSMJAA7239PH++KNbZOnWY8du5dlEZiQSVcbtYtYAAAAAADJzoQeA/WrvAAHJAYBQAACaKjdJscRn+wIAAAAABFla";

    [Fact]
    public async Task VerifiedAsset_ExtractsIntoInjectedRoot()
    {
        // 回归（2026-09-22 独立审计）：解压落点必须走注入根目录——单独变异 ExtractArchive 的
        // 目标（落回真实用户数据目录默认根）此前全套测试存活；缓存/日志等其余使用点由
        // CS9113 编译防线与本断言的 LocateLibraryDir 部分共同守护
        var stub = new StubHttpHandler();
        var (archiveBytes, entryPath) = BuildFixtureArchive();
        var assetName = FfmpegLibraryResolver.BtbnAsset!.Value.Asset;
        var declared = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        stub.Map(ChecksumsUrl, $"{declared}  *{assetName}\n");
        stub.Map(AssetUrl, archiveBytes);
        var root = _tempDir.FilePath("root-happy");
        var resolver = CreateResolver(stub, root);

        await resolver.DownloadAndExtract(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(root, entryPath)));
        Assert.NotNull(FfmpegLibraryResolver.LocateLibraryDir(root));
    }

    /// <summary>构造与平台资产格式配套的微型归档：Windows（zip 资产）测试内现造 zip；
    /// Linux（tar.xz 资产）用预生成夹具（macOS 等平台无 BtbnAsset，本测试类不覆盖）。</summary>
    private static (byte[] Archive, string EntryPath) BuildFixtureArchive()
    {
        const string entryPath = "ffmpeg-n9.0/lib/libavcodec.so.63";
        if (OperatingSystem.IsWindows())
        {
            using var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(
                ms, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(entryPath);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("fake-lib");
            }

            return (ms.ToArray(), entryPath);
        }

        return (Convert.FromBase64String(TarXzFixtureBase64), entryPath);
    }
}
