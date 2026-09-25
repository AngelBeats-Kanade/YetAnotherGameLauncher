using System.Security.Cryptography;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 背景图加载：本地路径 / URL / 失败回退 / 缓存。
/// 审计修复（2026-09-19，两次实测校准）：
/// 1) Dispatch(async ...) 吞断言（Func&lt;Task&gt; 重载不等待不传播，探针实锤，规则见 AGENTS.md）；
/// 2) Bitmap 解码只能在会话 UI 线程（渲染接口不在测试线程的 locator 里，直调 InvalidOperationException
///    被服务的静默回退吞掉——后者本身就是被测语义的一部分）；
/// 3) 不 await Dispatch(Action) 时 lambda 排队未跑，读到的局部变量恒为初值。
/// 现行形态：async Task 测试方法 await Dispatch(同步 lambda)；lambda 内启动服务调用后用 RunJobs
/// 泵到完成（无 await），结果存局部变量；断言一律在 Dispatch 之外；TempDir 生命周期随测试方法体。
/// </summary>
public sealed class BackgroundImageServiceTests
{
    [Fact]
    public void DefaultDiskCacheRoot_LivesUnderDataDirectory_NotConfigDirectory()
    {
        // 2026-09-22 路径策略：可重建缓存（http 图标/背景图）归数据目录，
        // 配置目录只留 games.json 等不可清理物
        Assert.Equal(
            Path.Combine(AppPaths.DataDirectory, "image-cache"), BackgroundImageService.DefaultDiskCacheRoot);
        Assert.False(BackgroundImageService.DefaultDiskCacheRoot.StartsWith(
            AppPaths.ConfigDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    /// <summary>1×1 PNG。</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    /// <summary>
    /// 在会话 UI 线程上启动服务调用并同步等待完成（RunJobs 泵异步续体；10s 上限防挂死）。
    /// 必须在 Dispatch 的同步 lambda 内调用：Bitmap 解码依赖会话线程的 IPlatformRenderInterface。
    /// </summary>
    private static IImage? RunToCompletion(Func<Task<IImage?>> call)
    {
        var task = call();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        Assert.True(task.IsCompleted, "服务调用 10s 内未完成（RunJobs 泵停摆）");
        return task.GetAwaiter().GetResult();
    }

    [Fact]
    public async Task LoadAsync_LocalFile_ReturnsImage()
    {
        _ = HeadlessSession.Instance;
        using var dir = new TempDir();
        var path = dir.FilePath("bg.png");
        File.WriteAllBytes(path, Png);
        var service = new BackgroundImageService(new HttpClient(new StubHttpHandler()));

        IImage? image = null;
        await HeadlessSession.Instance.Dispatch(() => image = RunToCompletion(() => service.LoadAsync(path)), CancellationToken.None);

        Assert.NotNull(image);
    }

    [Fact]
    public async Task LoadAsync_MissingLocalFile_ReturnsNull()
    {
        _ = HeadlessSession.Instance;
        using var dir = new TempDir();
        var service = new BackgroundImageService(new HttpClient(new StubHttpHandler()));

        IImage? image = null;
        await HeadlessSession.Instance.Dispatch(() => image = RunToCompletion(() => service.LoadAsync(dir.FilePath("nope.png"))), CancellationToken.None);

        Assert.Null(image);
    }

    [Fact]
    public async Task LoadAsync_HttpSource_ReturnsImageAndCaches()
    {
        _ = HeadlessSession.Instance;
        using var dir = new TempDir();
        var handler = new StubHttpHandler();
        handler.Map("https://cdn.example/bg.png", Png);
        // http 成功路径会写磁盘缓存：不注入临时根会写真机用户目录（2026-09-22 评审实锤遗留）
        var service = new BackgroundImageService(new HttpClient(handler), diskCacheRoot: dir.FilePath("image-cache"));

        IImage? first = null, second = null;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            first = RunToCompletion(() => service.LoadAsync("https://cdn.example/bg.png"));
            second = RunToCompletion(() => service.LoadAsync("https://cdn.example/bg.png"));
        }, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second); // 会话内缓存
    }

    [Fact]
    public async Task LoadAsync_Http_CleansStaleTemp_ButKeepsFreshInFlightTemp()
    {
        // 次级 suspect（第 9 轮，artifacts/bugs.md）：写前清理同源临时文件无差别删除——并发写盘方
        // （同源二次加载）的在途临时文件被删则对方那一轮缓存写入静默丢失。修复 = 只清理超过
        // 1 小时的旧残留（崩溃遗留与在途写入以 mtime 阈值区分）
        _ = HeadlessSession.Instance;
        using var dir = new TempDir();
        var cacheRoot = dir.FilePath("image-cache");
        var handler = new StubHttpHandler();
        handler.Map("https://cdn.example/stale.png", Png);
        var service = new BackgroundImageService(new HttpClient(handler), diskCacheRoot: cacheRoot);

        // 预置同源键的临时文件对：2 小时前的崩溃残留 + 并发写盘方的在途新鲜临时文件
        //（DiskCachePath = URL 的 SHA256 大写十六进制 + 扩展名，与实现同一拼装）
        Directory.CreateDirectory(cacheRoot);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("https://cdn.example/stale.png"))) + ".png";
        var stale = Path.Combine(cacheRoot, key + ".download-aaaa");
        var fresh = Path.Combine(cacheRoot, key + ".download-bbbb");
        await File.WriteAllBytesAsync(stale, [1, 2, 3]);
        await File.WriteAllBytesAsync(fresh, [4, 5, 6]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(2));

        await HeadlessSession.Instance.Dispatch(() =>
        {
            RunToCompletion(() => service.LoadAsync("https://cdn.example/stale.png"));
        }, CancellationToken.None);

        Assert.False(File.Exists(stale), "崩溃残留应被清理");
        Assert.True(File.Exists(fresh), "红落此断言：旧形态无差别删除连在途临时文件一起清");
    }

    [Fact]
    public async Task LoadAsync_Http_SecondInstance_ServedFromDiskCache()
    {
        _ = HeadlessSession.Instance;
        using var dir = new TempDir();
        var cacheRoot = dir.FilePath("image-cache");
        var handler = new StubHttpHandler();
        handler.Map("https://cdn.example/icon.png", Png);
        var first = new BackgroundImageService(new HttpClient(handler), diskCacheRoot: cacheRoot);
        var second = new BackgroundImageService(new HttpClient(handler), diskCacheRoot: cacheRoot);

        IImage? warmup = null, image = null;
        var requestCount = -1;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            warmup = RunToCompletion(() => first.LoadAsync("https://cdn.example/icon.png"));
            // 新实例（模拟重启）：内存缓存已空，命中磁盘缓存，零网络
            image = RunToCompletion(() => second.LoadAsync("https://cdn.example/icon.png"));
            requestCount = handler.Requests.Count;
        }, CancellationToken.None);

        Assert.NotNull(warmup);
        Assert.NotNull(image);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task ReloadAsync_BypassesCaches_RefetchesAndRewritesDisk()
    {
        _ = HeadlessSession.Instance;
        using var dir = new TempDir();
        var cacheRoot = dir.FilePath("image-cache");
        var handler = new StubHttpHandler();
        handler.Map("https://cdn.example/icon.png", Png);
        var service = new BackgroundImageService(new HttpClient(handler), diskCacheRoot: cacheRoot);

        IImage? warmup = null, reloaded = null;
        var requestCount = -1;
        byte[] diskBytes = [];
        await HeadlessSession.Instance.Dispatch(() =>
        {
            warmup = RunToCompletion(() => service.LoadAsync("https://cdn.example/icon.png"));
            // 绕过内存与磁盘缓存强制重取；重取结果回写磁盘缓存
            reloaded = RunToCompletion(() => service.ReloadAsync("https://cdn.example/icon.png"));
            requestCount = handler.Requests.Count;
            diskBytes = File.ReadAllBytes(Directory.GetFiles(cacheRoot).Single());
        }, CancellationToken.None);

        Assert.NotNull(warmup);
        Assert.NotNull(reloaded);
        Assert.Equal(2, requestCount);
        Assert.Equal(Png, diskBytes);
    }

    [Fact]
    public async Task LoadAsync_Http_PoisonedNetworkBytes_SelfHealsViaNetwork()
    {
        _ = HeadlessSession.Instance;
        using var dir = new TempDir();
        var cacheRoot = dir.FilePath("image-cache");
        var handler = new StubHttpHandler();
        handler.Map("https://cdn.example/icon.png", [1, 2, 3]); // CDN 返回 200 + 非图片字节
        var service = new BackgroundImageService(
            new HttpClient(handler), failureRetryInterval: TimeSpan.Zero, diskCacheRoot: cacheRoot);

        IImage? poisoned = null, healed = null;
        var staleEntries = -1;
        var requestCount = -1;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            poisoned = RunToCompletion(() => service.LoadAsync("https://cdn.example/icon.png"));
            staleEntries = Directory.GetFiles(cacheRoot).Length; // 坏字节未残留为缓存条目

            handler.Map("https://cdn.example/icon.png", Png); // CDN 恢复正常
            healed = RunToCompletion(() => service.LoadAsync("https://cdn.example/icon.png")); // 回落网络重下成功
            requestCount = handler.Requests.Count;
        }, CancellationToken.None);

        Assert.Null(poisoned); // 解码失败
        Assert.Equal(0, staleEntries);
        Assert.NotNull(healed);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task LoadAsync_Http_CorruptCacheFile_DeletedAndRefetched()
    {
        _ = HeadlessSession.Instance;
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
        File.WriteAllBytes(poisonedPath, [1, 2, 3]);

        IImage? first = null, second = null;
        var poisonedStillThere = true;
        var firstRoundRequests = -1;
        var secondRoundRequests = -1;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            first = RunToCompletion(() => service.LoadAsync("https://cdn.example/icon.png"));
            poisonedStillThere = File.Exists(poisonedPath); // 毒文件已被失效删除
            firstRoundRequests = handler.Requests.Count; // 本次未走网络（命中磁盘）

            second = RunToCompletion(() => service.LoadAsync("https://cdn.example/icon.png")); // 下次回落网络成功
            secondRoundRequests = handler.Requests.Count;
        }, CancellationToken.None);

        Assert.Null(first); // 命中坏文件解码失败
        Assert.False(poisonedStillThere);
        Assert.Equal(0, firstRoundRequests);
        Assert.NotNull(second);
        Assert.Equal(1, secondRoundRequests);
    }

    [Fact]
    public async Task LoadAsync_Http_CleansStaleTempsOnWrite()
    {
        _ = HeadlessSession.Instance;
        using var dir = new TempDir();
        var cacheRoot = dir.FilePath("image-cache");
        var handler = new StubHttpHandler();
        handler.Map("https://cdn.example/icon.png", Png);
        var service = new BackgroundImageService(new HttpClient(handler), diskCacheRoot: cacheRoot);

        // 模拟上次崩溃在改名前遗留的临时半成品。mtime 设为 2 小时前：写前清理现按"超过 1 小时
        // 的旧残留"判定（次级 suspect 第 9 轮——新鲜临时文件可能是并发写盘方的在途文件，
        // 无差别删除会毁掉对方那一轮写入；fresh 存活面由 CleansStaleTemp_ButKeepsFreshInFlightTemp 钉住）
        Directory.CreateDirectory(cacheRoot);
        var staleTemp = Path.Combine(cacheRoot, Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("https://cdn.example/icon.png"))) + ".png.download-stale");
        File.WriteAllBytes(staleTemp, [9]);
        File.SetLastWriteTimeUtc(staleTemp, DateTime.UtcNow - TimeSpan.FromHours(2));

        IImage? image = null;
        var staleStillThere = true;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            image = RunToCompletion(() => service.LoadAsync("https://cdn.example/icon.png"));
            staleStillThere = File.Exists(staleTemp); // 遗留临时文件已被写前清理
        }, CancellationToken.None);

        Assert.NotNull(image);
        Assert.False(staleStillThere);
    }

    [Fact]
    public async Task LoadAsync_HttpFailure_ReturnsNull()
    {
        _ = HeadlessSession.Instance;
        var handler = new StubHttpHandler { FailFirstN = 3 };
        var service = new BackgroundImageService(new HttpClient(handler));

        IImage? image = null;
        await HeadlessSession.Instance.Dispatch(() => image = RunToCompletion(() => service.LoadAsync("https://cdn.example/missing.png")), CancellationToken.None);

        Assert.Null(image);
    }

    [Fact]
    public async Task LoadAsync_Http_Timeout_ReturnsNull()
    {
        // 请求超时（HttpClient.Timeout / 连接超时 → TaskCanceledException 且外部 token 未取消）
        // 与 HttpRequestException 同属瞬时网络失败，必须同样按"装饰性资源失败"静默回退，
        // 而不是逃出服务契约（LoadAppBackgroundAsync 是弃元调用，逃逸即未观察任务异常）
        _ = HeadlessSession.Instance;
        var handler = new StubHttpHandler { TimeoutFirstN = 1 };
        var service = new BackgroundImageService(new HttpClient(handler));

        IImage? image = null;
        Exception? escaped = null;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            try
            {
                image = RunToCompletion(() => service.LoadAsync("https://cdn.example/slow.png"));
            }
            catch (Exception ex)
            {
                escaped = ex;
            }
        }, CancellationToken.None);

        Assert.Null(escaped); // 红落此断言：超时异常逃出服务契约，类型见失败消息
        Assert.Null(image);
    }

    [Fact]
    public async Task LoadAsync_MalformedAvaresSource_ReturnsNull()
    {
        // 畸形 avares URI（截断成 "avares://" 的配置手误）在 new Uri 处抛 UriFormatException，
        // 与缺资产（FileNotFoundException，已覆盖）不同，不在原过滤器内——逃逸即破坏"来源无效回退 null"
        _ = HeadlessSession.Instance;
        var service = new BackgroundImageService(new HttpClient(new StubHttpHandler()));

        IImage? image = null;
        Exception? escaped = null;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            try
            {
                image = RunToCompletion(() => service.LoadAsync("avares://"));
            }
            catch (Exception ex)
            {
                escaped = ex;
            }
        }, CancellationToken.None);

        Assert.Null(escaped); // 红落此断言：逃逸类型见失败消息
        Assert.Null(image);
    }

    [Fact]
    public async Task LoadAsync_LocalFile_ReadDenied_ReturnsNull()
    {
        // 本地文件无读权限（UnauthorizedAccessException）与不存在同属"来源不可用"。
        // POSIX 权限位可确定性构造；Windows 侧 ACL 拒读无法跨平台等价布置，由本腿独占覆盖
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("UnauthorizedAccessException 注入依赖 POSIX 权限位，Windows 无法确定性构造");
        }

        var service = new BackgroundImageService(new HttpClient(new StubHttpHandler()));
        using var dir = new TempDir();

        // root/CAP_DAC_OVERRIDE 豁免 DAC：拒读构造不出时测试空心化（解码失败分支同样返回
        // null，两形态不可区分）——同族前提探针
        if (DacExemptionProbe.Exempt(dir.Path))
        {
            Assert.Skip("当前进程可无视权限位（root/CAP_DAC_OVERRIDE），拒读形态不成立");
        }

        var path = dir.FilePath("denied.png");
        File.WriteAllBytes(path, [0x89]);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
        }

        Exception? escaped = null;
        IImage? image = null;
        try
        {
            image = await service.LoadAsync(path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            escaped = ex;
        }

        Assert.Null(escaped); // 拒读异常不得逃出服务契约
        Assert.Null(image);
    }

    [Fact]
    public async Task LoadAsync_EmptySource_ReturnsNull()
    {
        var service = new BackgroundImageService(new HttpClient(new StubHttpHandler()));

        Assert.Null(await service.LoadAsync(""));
        Assert.Null(await service.LoadAsync(null));
    }

    [Fact]
    public async Task Forget_RemovesCacheEntry_SoChangedSourcesDoNotPinOldPixels()
    {
        // F35（artifacts/bugs.md）：单例缓存字典与应用同寿——来源 URL 变更后旧条目永久强可达
        // （finalizer 兜不住）。调用方（游戏列表重建/应用背景来源变更）在旧来源不再被引用时
        // Forget。所有权契约：Forget 不 Dispose 位图（释放走 UI 侧宽限退役）
        _ = HeadlessSession.Instance;
        using var dir = new TempDir();
        var pngPath = dir.FilePath("icon.png");
        File.WriteAllBytes(pngPath, Png);
        var service = new BackgroundImageService(new HttpClient(new StubHttpHandler()));

        IImage? loaded = null;
        bool cachedBefore = true, cachedAfter = true;
        await HeadlessSession.Instance.Dispatch(() =>
        {
            loaded = RunToCompletion(() => service.LoadAsync(pngPath));
            cachedBefore = service.IsCachedForTests(pngPath);
            service.Forget(pngPath);
            cachedAfter = service.IsCachedForTests(pngPath);
        }, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.True(cachedBefore);
        Assert.False(cachedAfter); // 红落此断言：条目必须被移除
    }
}
