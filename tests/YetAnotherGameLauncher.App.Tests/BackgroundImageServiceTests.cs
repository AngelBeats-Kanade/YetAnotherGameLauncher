using System.Security.Cryptography;
using System.Text;
using Xunit;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>背景图加载：本地路径 / URL / 失败回退 / 缓存。</summary>
public class BackgroundImageServiceTests
{
    /// <summary>1×1 PNG。</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [Fact]
    public async Task LoadAsync_LocalFile_ReturnsImage()
    {
        // Bitmap 解码依赖 Avalonia 引擎，需在 headless 会话内执行
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            using var dir = new TempDir();
            var path = dir.FilePath("bg.png");
            await File.WriteAllBytesAsync(path, Png);
            var service = new BackgroundImageService(new HttpClient(new StubHttpHandler()));

            var image = await service.LoadAsync(path);

            Assert.NotNull(image);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_MissingLocalFile_ReturnsNull()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            using var dir = new TempDir();
            var service = new BackgroundImageService(new HttpClient(new StubHttpHandler()));

            var image = await service.LoadAsync(dir.FilePath("nope.png"));

            Assert.Null(image);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_HttpSource_ReturnsImageAndCaches()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            var handler = new StubHttpHandler();
            handler.Map("https://cdn.example/bg.png", Png);
            var service = new BackgroundImageService(new HttpClient(handler));

            var first = await service.LoadAsync("https://cdn.example/bg.png");
            var second = await service.LoadAsync("https://cdn.example/bg.png");

            Assert.NotNull(first);
            Assert.Same(first, second); // 会话内缓存
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_Http_SecondInstance_ServedFromDiskCache()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            using var dir = new TempDir();
            var cacheRoot = dir.FilePath("image-cache");
            var handler = new StubHttpHandler();
            handler.Map("https://cdn.example/icon.png", Png);
            var first = new BackgroundImageService(new HttpClient(handler), diskCacheRoot: cacheRoot);
            Assert.NotNull(await first.LoadAsync("https://cdn.example/icon.png"));

            // 新实例（模拟重启）：内存缓存已空，命中磁盘缓存，零网络
            var second = new BackgroundImageService(new HttpClient(handler), diskCacheRoot: cacheRoot);
            var image = await second.LoadAsync("https://cdn.example/icon.png");

            Assert.NotNull(image);
            Assert.Single(handler.Requests);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReloadAsync_BypassesCaches_RefetchesAndRewritesDisk()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            using var dir = new TempDir();
            var cacheRoot = dir.FilePath("image-cache");
            var handler = new StubHttpHandler();
            handler.Map("https://cdn.example/icon.png", Png);
            var service = new BackgroundImageService(new HttpClient(handler), diskCacheRoot: cacheRoot);
            Assert.NotNull(await service.LoadAsync("https://cdn.example/icon.png"));

            var reloaded = await service.ReloadAsync("https://cdn.example/icon.png");

            // 绕过内存与磁盘缓存强制重取；重取结果回写磁盘缓存
            Assert.NotNull(reloaded);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(Png, await File.ReadAllBytesAsync(Directory.GetFiles(cacheRoot).Single()));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_Http_PoisonedNetworkBytes_SelfHealsViaNetwork()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            using var dir = new TempDir();
            var cacheRoot = dir.FilePath("image-cache");
            var handler = new StubHttpHandler();
            handler.Map("https://cdn.example/icon.png", [1, 2, 3]); // CDN 返回 200 + 非图片字节
            var service = new BackgroundImageService(
                new HttpClient(handler), failureRetryInterval: TimeSpan.Zero, diskCacheRoot: cacheRoot);

            Assert.Null(await service.LoadAsync("https://cdn.example/icon.png")); // 解码失败
            Assert.Empty(Directory.GetFiles(cacheRoot)); // 坏字节未残留为缓存条目

            handler.Map("https://cdn.example/icon.png", Png); // CDN 恢复正常
            Assert.NotNull(await service.LoadAsync("https://cdn.example/icon.png")); // 回落网络重下成功
            Assert.Equal(2, handler.Requests.Count);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_Http_CorruptCacheFile_DeletedAndRefetched()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            using var dir = new TempDir();
            var cacheRoot = dir.FilePath("image-cache");
            var handler = new StubHttpHandler();
            handler.Map("https://cdn.example/icon.png", Png);
            var service = new BackgroundImageService(
                new HttpClient(handler), failureRetryInterval: TimeSpan.Zero, diskCacheRoot: cacheRoot);

            // 模拟外力/历史版本写坏的缓存文件（命名与实现一致：SHA256(url) + 扩展名）
            Directory.CreateDirectory(cacheRoot);
            var poisonedPath = Path.Combine(cacheRoot, Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("https://cdn.example/icon.png"))) + ".png");
            await File.WriteAllBytesAsync(poisonedPath, [1, 2, 3]);

            Assert.Null(await service.LoadAsync("https://cdn.example/icon.png")); // 命中坏文件解码失败
            Assert.False(File.Exists(poisonedPath)); // 毒文件已被失效删除
            Assert.Empty(handler.Requests); // 本次未走网络（命中磁盘）

            Assert.NotNull(await service.LoadAsync("https://cdn.example/icon.png")); // 下次回落网络成功
            Assert.Single(handler.Requests);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_Http_CleansStaleTempsOnWrite()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            using var dir = new TempDir();
            var cacheRoot = dir.FilePath("image-cache");
            var handler = new StubHttpHandler();
            handler.Map("https://cdn.example/icon.png", Png);
            var service = new BackgroundImageService(new HttpClient(handler), diskCacheRoot: cacheRoot);

            // 模拟上次崩溃在改名前遗留的临时半成品
            Directory.CreateDirectory(cacheRoot);
            var staleTemp = Path.Combine(cacheRoot, Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("https://cdn.example/icon.png"))) + ".png.download-stale");
            await File.WriteAllBytesAsync(staleTemp, [9]);

            Assert.NotNull(await service.LoadAsync("https://cdn.example/icon.png"));
            Assert.False(File.Exists(staleTemp)); // 遗留临时文件已被写前清理
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_HttpFailure_ReturnsNull()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            var handler = new StubHttpHandler { FailFirstN = 3 };
            var service = new BackgroundImageService(new HttpClient(handler));

            var image = await service.LoadAsync("https://cdn.example/missing.png");

            Assert.Null(image);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_EmptySource_ReturnsNull()
    {
        var service = new BackgroundImageService(new HttpClient(new StubHttpHandler()));

        Assert.Null(await service.LoadAsync(""));
        Assert.Null(await service.LoadAsync(null));
    }
}
