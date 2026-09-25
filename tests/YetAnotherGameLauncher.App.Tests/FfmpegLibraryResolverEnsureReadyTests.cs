using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// FFmpeg 首运解析 <c>EnsureReady</c> 的两条纪律回归（2026-09-24 自主维护轮）：
/// ① 下载目录 TryBind 失败即返回——绑定一次性初始化在 TryBind 内已锁死，失败后落穿
///   系统探测/整包下载全都无法挽回，只会白付 60–70MB 流量（夹具假库绑定必失败或落系统回退）；
/// ② 并发单飞——首运下载分钟级窗口内第二个 transient 播放器并发进入，不得重复下载/竞态解压。
/// 真实触碰绑定（TryBind→dlopen）：必须进 sequential 集合（AGENTS.md 纪律）。
/// 系统库探测注入恒 false：消除"机器装没装配套 FFmpeg 9.0"的分支差异，双平台 CI 确定性同路径。
/// **盲区声明（DEVELOPMENT.md §3.6 规则 1）**：本类全部用例走"绑定失败"路径（夹具是假库字节），
/// "绑定成功后目录预载/多库探测真正可用"无法离线覆盖（真绑定每进程一次）——该路径的验证方式
/// 是真机冒烟（跑应用看 FFmpeg libraries ready + 解码器协商日志）；2026-09-24 P0 即从此盲区穿过。
/// </summary>
[Collection("sequential")]
public class FfmpegLibraryResolverEnsureReadyTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    /// <summary>checksums 校验文件的完整下载地址（与生产同一事实源）。</summary>
    private static string ChecksumsUrl => $"{FfmpegLibraryResolver.BtbnBaseUrl}/checksums.sha256";

    /// <summary>本平台资产的完整下载地址（同上）。</summary>
    private static string AssetUrl =>
        $"{FfmpegLibraryResolver.BtbnBaseUrl}/{FfmpegLibraryResolver.BtbnAsset!.Value.Asset}";

    /// <summary>构造走注入 client、注入根目录与恒 false 系统探测的 resolver（logger 可选注入）。</summary>
    private FfmpegLibraryResolver CreateResolver(
        StubHttpHandler stub, string root, CollectingLogger? logger = null) => new(
        new NetworkProxyManager(),
        logger: logger,
        downloadClient: new HttpClient(stub),
        downloadRoot: root,
        systemLibraryProbe: () => false);

    [Fact]
    public void EnsureReady_MissingLibraries_ResolveEachLibraryAtMostOnce()
    {
        // 机器前提（复review R1，2026-09-24 实证）：本用例构造"目录残缺 + 系统无配套库"的缺失形态，
        // 断言 EnsureReady 必败。注入的 systemLibraryProbe 只门分支②，管不住 resolver 内部的
        // 系统回退——机器真实装有精确配套 FFmpeg 9（LD_LIBRARY_PATH 指向 soname 链接实测复现）
        // 时绑定会合法成功，Assert.False 假红。同款守卫见上方 JunkDownloadedDir 用例
        if (System.Runtime.InteropServices.NativeLibrary.TryLoad(
                OperatingSystem.IsWindows() ? "avcodec-63.dll" : "libavcodec.so.63", out _))
        {
            Assert.Skip("机器装有精确配套的 FFmpeg 9 avcodec——'缺失形态'前提不成立");
        }

        // M3+M4 负缓存回归（2026-09-24 review 立项）：目录残缺（只含垃圾 avcodec）时，每个库的
        // 全链解析（目录→系统精确版本→系统裸名）必须恰好执行一次——失败句柄同样缓存（恢复
        // "失败句柄也缓存避免反复尝试"的旧纪律），此后就绪探测一律命中缓存不再重扫。
        // 红实证（2026-09-24）：旧形态计数=1——预载只扫目录不计系统回退、首个探测失败即中止
        // TryBind，"每库恰一次全链解析"的不变量在旧结构里根本不存在。
        // 夹具带完成标记（F26 后无标记目录会被先删重下、到不了绑定）——本用例的主语是负缓存，
        // 必须走"被信任的目录"路径
        var stub = new StubHttpHandler();
        var root = _tempDir.FilePath("root-negcache");
        var libDir = Path.Combine(root, "ffmpeg-n9.0", "lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllBytes(Path.Combine(libDir, "libavcodec.so.63"), "junk-not-an-elf"u8.ToArray());
        File.WriteAllText(Path.Combine(libDir, FfmpegLibraryResolver.CompletionMarkerFileName), "ok");
        var resolver = CreateResolver(stub, root);

        // 绑定必败（垃圾库 + 系统探测恒 false）：状态烧毁不重试，首个探测即触发预载与计数
        Assert.False(resolver.EnsureReady(CancellationToken.None));

        Assert.Equal(
            FfmpegLibraryResolver.LibraryDependencyOrder.Length,
            resolver.DirectoryResolutionAttemptsForTests);
    }

    [Fact]
    public void EnsureReady_JunkDownloadedDir_DoesNotBindThroughMismatchedSystemLibrary()
    {
        // 系统回退纪律（宁缺毋滥）：目录内垃圾库 + 系统只有无版本 .so 符号链接（指向其它大版本）
        // 时必须放弃绑定——.so 链接指向发行版当前系列（本机实测 →.so.62，绑定要 63），
        // av_version_info 恰好 ABI 稳定不崩、首个结构体调用处即崩，不得经符号链接误绑
        if (!OperatingSystem.IsLinux()
            || !System.Runtime.InteropServices.NativeLibrary.TryLoad("libavcodec.so", out _)
            || System.Runtime.InteropServices.NativeLibrary.TryLoad("libavcodec.so.63", out _))
        {
            Assert.Skip("需要 Linux 且系统存在 libavcodec.so 符号链接、无精确 .so.63 的先决");
        }

        var stub = new StubHttpHandler();
        var root = _tempDir.FilePath("root-symlink");
        var libDir = Path.Combine(root, "ffmpeg-n9.0", "lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllBytes(Path.Combine(libDir, "libavcodec.so.63"), "junk-not-an-elf"u8.ToArray());
        File.WriteAllText(Path.Combine(libDir, FfmpegLibraryResolver.CompletionMarkerFileName), "ok"); // F26：无标记会被先删
        var resolver = CreateResolver(stub, root);

        var result = resolver.EnsureReady(CancellationToken.None);

        Assert.False(result); // 红落此断言：经 .so 符号链接误绑错版本库时为 true
    }

    [Fact]
    public void EnsureReady_MarkedDirBindFails_DoesNotFallThroughToDownload()
    {
        // ① 命中一个"带完成标记、有库文件但全是垃圾字节"的下载目录：TryBind 实际尝试绑定并失败，
        // 一次性初始化即烧。核心不变量=此后零网络（绝不落穿②③再发起下载）——F26 后本不变量
        // 只对**带标记**目录成立（无标记毒目录必须删除重下自愈，见 UnmarkedPoisonedDir 用例）。
        // 返回值 true/false 随机器系统库差异合法波动（.so.63 系统上目录内垃圾失败后
        // DirectoryFunctionResolver 的系统回退可成功绑定），故不断言结果、只断言零网络
        var stub = new StubHttpHandler();
        var root = _tempDir.FilePath("root-poisoned");
        var libDir = Path.Combine(root, "ffmpeg-n9.0", "lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllBytes(Path.Combine(libDir, "libavcodec.so.63"), "junk-not-an-elf"u8.ToArray());
        File.WriteAllText(Path.Combine(libDir, FfmpegLibraryResolver.CompletionMarkerFileName), "ok");
        var resolver = CreateResolver(stub, root);

        // 前置：夹具目录必须可定位。全量套件中本测试曾出现一次"落穿形状"的 flake
        // （requests=1，机制未定位）——若再发，先看此断言：dir 定位失败会走②③，
        // 把"目录没了"误报成"落穿没修"
        Assert.NotNull(FfmpegLibraryResolver.LocateLibraryDir(root));

        var thrown = Record.Exception(() => resolver.EnsureReady(CancellationToken.None));

        Assert.Null(thrown); // EnsureReady 全失败路径内部消化，不得向调用方抛出
        Assert.Empty(stub.Requests); // 红落此断言：落穿③时此处的 checksums 请求即证据
        Assert.True(Directory.Exists(libDir)); // 带标记的完整目录绑定失败不删除（防无限重下）
    }

    [Fact]
    public void EnsureReady_UnmarkedPoisonedDir_SelfHealsByDeletionAndRedownload()
    {
        // F26（artifacts/bugs.md）：解压中断毒化的库目录（磁盘满/进程被杀留下半套库）会让
        // TryBind 一次性锁死，重启也不自愈——只能手删 data/ffmpeg/<rid>。修复后：无完成标记的
        // 库目录在绑定尝试**前**删除（一次性初始化未烧），本会话即落穿②③重下自愈；带标记的
        // 完整目录绑定失败才保留永久锁死（防不兼容环境无限重下）。机器前提同 MissingLibraries
        if (System.Runtime.InteropServices.NativeLibrary.TryLoad(
                OperatingSystem.IsWindows() ? "avcodec-63.dll" : "libavcodec.so.63", out _))
        {
            Assert.Skip("机器装有精确配套的 FFmpeg 9 avcodec——'假库绑定必败'前提不成立");
        }

        var stub = new StubHttpHandler();
        var (archiveBytes, _) = TestFfmpegArchive.Create();
        var assetName = FfmpegLibraryResolver.BtbnAsset!.Value.Asset;
        var declared = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(archiveBytes)).ToLowerInvariant();
        stub.Map(ChecksumsUrl, $"{declared}  *{assetName}\n");
        stub.Map(AssetUrl, archiveBytes);
        var root = _tempDir.FilePath("root-unmarked-poison");
        var libDir = Path.Combine(root, "ffmpeg-n9.0", "lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllBytes(Path.Combine(libDir, "libavcodec.so.63"), "junk-not-an-elf"u8.ToArray());
        var resolver = CreateResolver(stub, root);

        var result = resolver.EnsureReady(CancellationToken.None);

        // 毒化目录已被删除（自愈前提）；红形态=目录原样保留、零下载（旧"绑定失败即整体失败"不变量）
        Assert.False(Directory.Exists(libDir), "无标记毒目录必须在绑定尝试前删除");
        Assert.Equal(2, stub.Requests.Count); // checksums + 资产各一次：落穿③重下发生了
        var healedDir = FfmpegLibraryResolver.LocateLibraryDir(root);
        Assert.NotNull(healedDir);
        Assert.True(File.Exists(Path.Combine(healedDir!, FfmpegLibraryResolver.CompletionMarkerFileName)),
            "重下的安装必须携带完成标记");
        Assert.False(result); // 夹具是假库字节：重下后绑定仍必败（本进程内无法二次烧初始化）

        // 永久锁死语义对"带标记的完整目录"保持：二次调用零新增请求（防不兼容环境无限重下）
        resolver.EnsureReady(CancellationToken.None);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task EnsureReady_ConcurrentFirstRun_DownloadsExactlyOnce()
    {
        var stub = new StubHttpHandler();
        var (archiveBytes, _) = TestFfmpegArchive.Create();
        var assetName = FfmpegLibraryResolver.BtbnAsset!.Value.Asset;
        var declared = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(archiveBytes)).ToLowerInvariant();
        stub.Map(ChecksumsUrl, $"{declared}  *{assetName}\n");
        stub.Map(AssetUrl, archiveBytes);
        var logger = new CollectingLogger();
        var resolver = CreateResolver(stub, _tempDir.FilePath("root-race"), logger);

        // 确定性并发窗口：观察到第一个调用方的 checksums 已在途（状态未落定）再启动第二个，
        // 不靠时序运气。不用门控 TCS 钉请求——其续体调度在测试共享进程内不可靠（实锤卡死），
        // 请求在途本身就是足够宽的窗口（校验→下载→解压→绑定之间有多个真实 await）
        var first = Task.Run(() => resolver.EnsureReady(CancellationToken.None));
        Assert.True(
            SpinWait.SpinUntil(() => stub.Requests.Count >= 1, TimeSpan.FromSeconds(10)),
            "第一个调用方未在期限内发起 checksums 请求");
        var second = Task.Run(() => resolver.EnsureReady(CancellationToken.None));

        try
        {
            // 看门狗：并发缺陷若表现为卡死，60s 内以 TimeoutException 失败而非挂起整个测试进程
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"并发调用 60s 未落定：first={first.Status} second={second.Status} " +
                $"requests={stub.Requests.Count} 日志=[{string.Join(" | ", logger.Messages)}]");
        }

        // 单飞不变量：整包下载序列恰好一次（checksums + 资产各一次）。
        // 修复前两调用方各自完整下载 = 4 次请求；结果 true/false 不断言（随系统库差异合法波动）
        Assert.Equal(2, stub.Requests.Count);
    }

    /// <summary>收集 resolver 日志的最小 ILogger（卡死取证用，先例 SystemProcessRunnerTests）。</summary>
    private sealed class CollectingLogger : Microsoft.Extensions.Logging.ILogger<FfmpegLibraryResolver>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add($"{logLevel}:{formatter(state, exception)}" +
                             (exception is null ? "" : $" ← {exception.GetType().Name}: {exception.Message}"));
            }
        }
    }
}
