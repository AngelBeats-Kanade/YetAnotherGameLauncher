using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using SharpCompress.Readers;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using static FFmpeg.AutoGen.ffmpeg;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// FFmpeg 原生库准备器（FFmpeg.AutoGen 绑定 ↔ 原生库的版本必须配套，且绑定初始化只有一次机会）：
/// ① 复用应用数据目录里已下载的库；② 探测系统已安装的同版本 FFmpeg（Linux 桌面发行版常见，命中即零下载）；
/// ③ 都没有时从 BtbN FFmpeg-Builds 下载与绑定版本配套的 LGPL 共享构建（SHA256 校验后解压）。
/// 就绪判定 = 通过自定义 <see cref="IFunctionResolver"/> 实际调通 FFmpeg 版本 API。
/// </summary>
[ExcludeFromCodeCoverage]
public sealed partial class FfmpegLibraryResolver(
    NetworkProxyManager proxyManager,
    ILogger<FfmpegLibraryResolver>? logger = null,
    HttpClient? downloadClient = null,
    string? downloadRoot = null,
    Func<bool>? systemLibraryProbe = null)
{
    /// <summary>大文件下载专用 client：共享底层 handler（代理设置同步生效），仅放宽超时；
    /// 可注入（测试注入 stub client 实现离线，杜绝新环境真实下载约 60–70MB 的回退）。
    /// 注入方自理超时（不注入即 15 分钟专用超时；测试快路径用 client 默认超时即可）。</summary>
    private readonly HttpClient _downloadClient = downloadClient
        ?? new HttpClient(proxyManager.Handler)
        {
            Timeout = DownloadTimeout,
        };

    /// <summary>下载/解压目标根目录（实例缝）：默认 <see cref="DefaultDownloadRoot"/>，
    /// 测试注入临时目录以保证不写真实用户数据目录。</summary>
    private readonly string _downloadRoot = downloadRoot ?? DefaultDownloadRoot;

    /// <summary>系统库探测缝（可注入）：测试注入恒 false 消除"机器装没装配套 FFmpeg"的分支差异，
    /// 两平台 CI 确定性走下载路径；生产不注入即真实探测。</summary>
    private readonly Func<bool> _systemLibraryProbe = systemLibraryProbe ?? SystemLibraryPresent;

    /// <summary>单飞门：首运解析全同步（内部 GetResult），Monitor 保证并发调用方（多路 transient
    /// 播放器同帧起播）串行进入——否则首运下载分钟级窗口内重复下载 60–70MB/竞态解压同一目录。
    /// 语义声明（M2，2026-09-24 review 立项）：Monitor 等待不可取消——下载窗口内并发进入的
    /// 调用方在锁上泊车至全程结束（≤15 分钟下载超时），期间其 cancellationToken 不被观察
    /// （Stop 只取消令牌、不解锁 Monitor）。解码线程为后台线程、UI 不受阻，属已接受权衡；
    /// 如需可取消，改为 Monitor.Wait 轮询 + token 检查的等待循环。</summary>
    private readonly object _ensureGate = new();

    /// <summary>下载源：与 FFmpeg.AutoGen 9.0.x 绑定配套的 FFmpeg 9.0 LGPL 共享构建（双平台）；
    /// internal 供测试从同一事实源构造 stub 映射 URL，不在测试里复制字符串。</summary>
    internal const string BtbnBaseUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download";

    /// <summary>下载大文件（linux tar.xz 约 59MB / win zip 约 72MB，GitHub API 2026-09-22 实测）
    /// 不能复用全局 30 秒超时的 HttpClient：专用慢速超时。</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    // 解析状态（_resolveState）：0=未尝试（下载失败后停留于此，允许下次重试；字段默认值即此态）/
    // 1=已就绪 / 2=永久失败（绑定已实际尝试，ffmpeg 类型的静态初始化只有一次机会，
    // 失败后的函数委托是不可重试的 throw stub）
    private const int ResolveReady = 1;
    private const int ResolveFailedPermanently = 2;

    /// <summary>解析状态（原子读写，取值见上方状态注释）。</summary>
    private int _resolveState;

    /// <summary>解析器单例：必须在 ffmpeg 类型首次触碰前注入，目录随后设置。</summary>
    private DirectoryFunctionResolver? _sharedResolver;

    /// <summary>下载源：按平台给出 BtbN 资产文件名与校验行；非 Win/Linux 返回 null（无下载支持）。
    /// internal 供测试从同一事实源取资产名构造 stub 映射。</summary>
    internal static (string Asset, string Pattern)? BtbnAsset =>
        OperatingSystem.IsWindows()
            ? ("ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip", @"([0-9a-f]{64})[ \t]+\*?ffmpeg-n9\.0-latest-win64-lgpl-shared-9\.0\.zip")
            : OperatingSystem.IsLinux()
                ? ("ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0.tar.xz", @"([0-9a-f]{64})[ \t]+\*?ffmpeg-n9\.0-latest-linux64-lgpl-shared-9\.0\.tar\.xz")
                : null;

    /// <summary>avcodec 库文件名模式（判断目录里是否真的有库）。</summary>
    [GeneratedRegex(@"^(lib)?avcodec(-\d+)?(\.so(\.\d+)*)?(\.dll)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AvcodecFileRegex();

    /// <summary>库目录完成标记文件名（位于 LocateLibraryDir 返回的库目录内）：EnsureReady 只信
    /// 带标记的目录——解压中断/旧布局遗留的无标记目录一律先删再重下（F26 自愈）；internal 供测试构造夹具。</summary>
    internal const string CompletionMarkerFileName = ".yagl-ffmpeg-complete";

    /// <summary>
    /// 确保解码能力就绪（必要时在后台执行首运下载）。返回 false = 本机无系统库且下载失败/不支持，
    /// 调用方保持静态海报。绑定一旦实际尝试过就不可重试（成败均锁定）；仅下载失败允许下次再试。
    /// </summary>
    public bool EnsureReady(CancellationToken cancellationToken)
    {
        var state = Volatile.Read(ref _resolveState);
        if (state == ResolveReady)
        {
            return true;
        }

        if (state == ResolveFailedPermanently)
        {
            return false;
        }

        lock (_ensureGate)
        {
            // 双检：等锁期间另一调用方可能已完成解析（成败都不要再走一遍）
            state = Volatile.Read(ref _resolveState);
            if (state == ResolveReady)
            {
                return true;
            }

            if (state == ResolveFailedPermanently)
            {
                return false;
            }

            return EnsureReadyCore(cancellationToken);
        }
    }

    /// <summary>测试观测：库全链解析（目录→系统）的执行次数。负缓存生效时恒等于
    /// <see cref="LibraryDependencyOrder"/> 的库数（每库至多一次）；零句柄被反复重扫则更多。</summary>
    internal int DirectoryResolutionAttemptsForTests => _sharedResolver?.ResolutionAttempts ?? -1;

    /// <summary>解析主体（调用方须持 <see cref="_ensureGate"/> 且状态为未尝试）。</summary>
    private bool EnsureReadyCore(CancellationToken cancellationToken)
    {
        // ffmpeg 类型的首次触碰（含读 LibraryVersionMap）会以"当时的 FunctionResolver"完成一次性绑定，
        // 因此解析器必须最先注入（共享实例、目录后置），此后才能触碰 ffmpeg 的任何成员
        _sharedResolver = new DirectoryFunctionResolver();
        DynamicallyLoadedBindings.FunctionResolver = _sharedResolver;

        // ① 已下载目录命中——只信带完成标记的目录：无标记 = 解压中断毒化（F26）或旧布局遗留，
        // 在其上尝试绑定会烧掉一次性初始化并永久锁死，必须先删（本会话即可落穿②③重下自愈）；
        // 带标记目录（SHA256 验过 + 解压完整）TryBind 失败即整体失败——完整库仍绑不上 = 环境不兼容，
        // 重下只会无限循环（2026-09-24 实测修复落穿后的语义，毒目录防线由标记检查承接）
        var dir = LocateLibraryDir(_downloadRoot);
        while (dir is not null && !HasCompletionMarker(dir))
        {
            logger?.LogInformation("Deleting incomplete FFmpeg library directory {Directory}", dir);
            if (!FileUtilities.TryDeleteDirectory(dir))
            {
                logger?.LogWarning(
                    "Cannot delete incomplete FFmpeg library directory {Directory}; skipping local libraries", dir);
                dir = null;
                break;
            }

            dir = LocateLibraryDir(_downloadRoot);
        }

        if (dir is not null)
        {
            return TryBind(dir);
        }

        // ② 系统已装与绑定精确同版本的 FFmpeg：文件名预检（不触碰 ffmpeg 类型），命中即零下载
        if (_systemLibraryProbe())
        {
            logger?.LogInformation("Using system-installed FFmpeg libraries");
            return TryBind(null);
        }

        // ③ 首运下载
        if (BtbnAsset is null)
        {
            logger?.LogInformation("No FFmpeg download support on this platform");
            Volatile.Write(ref _resolveState, ResolveFailedPermanently);
            return false;
        }

        try
        {
            DownloadAndExtract(cancellationToken).GetAwaiter().GetResult();
            dir = LocateLibraryDir(_downloadRoot);
            return dir is not null && TryBind(dir);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
            or TaskCanceledException or InvalidDataException
            or System.Security.Cryptography.CryptographicException)
        {
            // 下载/解压失败尚未触碰 ffmpeg 类型（TryBind 未执行），保持"未尝试"以便下次调用重试
            logger?.LogInformation(ex, "FFmpeg library download failed");
            return false;
        }
    }

    /// <summary>设定库目录并实际调用一次 FFmpeg API：全部导入可解析即版本配套。绑定只有一次机会，成败均锁定。</summary>
    private bool TryBind(string? directory)
    {
        Volatile.Write(ref _resolveState, ResolveFailedPermanently); // 先锁死：绑定尝试即烧掉一次性初始化
        _sharedResolver!.SetDirectory(directory);
        try
        {
            // 就绪判定必须覆盖真实调用面（N5 校正 2026-09-24）：avutil/swscale/avcodec/avformat 为
            // 播放器直调（调用家族 av_*/sws_*/avcodec_*/avformat_* grep 实测）；swresample 无直调
            // 但是 libavcodec 的 DT_NEEDED 传递依赖（readelf 实测）——不探则残缺 avcodec 照样过探针。
            // 只探 av_version_info 会把"其余库全没加载"误判成就绪——avutil 无同伴依赖总能独立加载、
            // av_version_info 恰好 ABI 稳定，故障被推迟到起播时以 NotSupportedException stub 爆出
            // （2026-09-24 本机实锤：EnsureReady True + avformat_open_input 抛 stub）。不探
            // avfilter/avdevice：播放器不用它们，最小系统安装可能缺件而误伤可用环境
            _ = av_version_info();
            _ = avcodec_version();
            _ = avformat_version();
            _ = swresample_version();
            _ = swscale_version();
            Volatile.Write(ref _resolveState, ResolveReady);
            logger?.LogInformation("FFmpeg libraries ready ({Source})", directory ?? "system");
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or NotSupportedException or TypeInitializationException)
        {
            logger?.LogInformation("FFmpeg binding failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 系统是否已装与绑定精确同版本的 FFmpeg：探测 avcodec 的配套主版本文件（文件名预检，
    /// 不触碰 ffmpeg 类型——读取其任何成员都会触发一次性绑定而烧掉目录注入机会）。
    /// Linux 必须带 so 版本号：加载其它主版本会因 ABI 不配套在结构体调用处崩溃，宁缺毋滥。
    /// </summary>
    private static bool SystemLibraryPresent() => NativeLibrary.TryLoad(
        OperatingSystem.IsWindows()
            ? BoundAvcodecFile
            : $"libavcodec.so.{BoundLibavMajor}",
        out _);

    /// <summary>绑定（FFmpeg.AutoGen 9.0.x ↔ FFmpeg 9.0）所需的 avcodec 文件名（Windows 形态）。</summary>
    private const string BoundAvcodecFile = "avcodec-63.dll";

    /// <summary>绑定所需的 libavcodec 主版本号（Linux 的 so 版本号，与 <see cref="BoundAvcodecFile"/> 同步改；
    /// 其它库的主版本经 <see cref="LibraryFileMajor"/> 逐库精确，不得复用本值）。</summary>
    private const int BoundLibavMajor = 63;

    /// <summary>
    /// 绑定版本（FFmpeg 9.0）各库的系统文件主版本号（2026-09-24 实测 BtbN LGPL shared 构建 soname：
    /// avutil=61、avfilter=12、swresample=7、swscale=10；avcodec/avformat/avdevice=63）。
    /// 各库主版本互不相同，探测/加载文件名必须逐库精确——avcodec 的 63 复用到其它库即必败
    /// （libavutil.so.61 ≠ .so.63，Windows 同理只有 avutil-61.dll）；未收录库名回退 avcodec 主版本。
    /// </summary>
    internal static int LibraryFileMajor(string libraryName) => libraryName switch
    {
        "avutil" => 61,
        "avfilter" => 12,
        "swresample" => 7,
        "swscale" => 10,
        _ => BoundLibavMajor,
    };

    /// <summary>下载 BtbN 资产（SHA256 校验）并解压到注入的根目录；internal 供离线直测
    /// （经注入的 stub client 与临时根目录，checksums 拉取/校验段可确定性覆盖）。</summary>
    internal async Task DownloadAndExtract(CancellationToken cancellationToken)
    {
        var (asset, checksumPattern) = BtbnAsset!.Value;
        Directory.CreateDirectory(_downloadRoot);

        var expected = await FetchExpectedSha256Async(asset, checksumPattern, cancellationToken).ConfigureAwait(false);
        var tempFile = Path.Combine(Path.GetTempPath(), $"yagl-ffmpeg-{Guid.NewGuid():N}{Path.GetExtension(asset)}");
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(DownloadTimeout);
                using var response = await _downloadClient.GetAsync(
                    $"{BtbnBaseUrl}/{asset}", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var http = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var file = File.Create(tempFile);
                await http.CopyToAsync(file, timeout.Token).ConfigureAwait(false);
            }

            await VerifySha256(tempFile, expected, cancellationToken).ConfigureAwait(false);
            // 解压进暂存目录、写完成标记、原子移入正式位——半套库永远到不了被信任的位置（F26）。
            // 暂存必须在 _downloadRoot 下保证同卷 rename；与 ExtractArchive 的沙箱/链接还原防线协同
            var staging = Path.Combine(_downloadRoot, $".staging-{Guid.NewGuid():N}");
            try
            {
                ExtractArchive(tempFile, staging);
                var libDir = LocateLibraryDir(staging)
                    ?? throw new InvalidDataException("Extracted FFmpeg archive has no recognisable library directory");
                File.WriteAllText(Path.Combine(libDir, CompletionMarkerFileName), "ok");
                var final = Path.Combine(_downloadRoot, "installed");
                if (Directory.Exists(final))
                {
                    FileUtilities.TryDeleteDirectory(final); // 残留旧安装（仅手动删除重装场景可达）
                }

                Directory.Move(staging, final);
                logger?.LogInformation("FFmpeg libraries installed to {Directory}", final);
            }
            finally
            {
                FileUtilities.DeleteQuiet(staging); // 失败清理半成品；成功路径 staging 已不存在，幂等
            }
        }
        finally
        {
            FileUtilities.DeleteQuiet(tempFile);
        }
    }

    /// <summary>取 checksums.sha256 里对应资产的哈希；校验文件拉不到时抛异常（宁可不装也不装来路不明的库）。</summary>
    private async Task<string> FetchExpectedSha256Async(
        string asset, string pattern, CancellationToken cancellationToken)
    {
        using var response = await _downloadClient.GetAsync($"{BtbnBaseUrl}/checksums.sha256", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var match = Regex.Match(text, pattern, RegexOptions.Multiline);
        return match.Success
            ? match.Groups[1].Value
            : throw new InvalidDataException($"checksums.sha256 has no entry for {asset}");
    }

    /// <summary>校验下载文件的 SHA256，不符抛异常（并阻止解压）。</summary>
    private static async Task VerifySha256(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        var actual = await Hashing.Sha256HexAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new System.Security.Cryptography.CryptographicException(
                $"FFmpeg package checksum mismatch: {actual} != {expectedSha256}");
        }
    }

    /// <summary>按扩展名解压（zip 用内置实现，tar.xz 用 SharpCompress），拒绝路径穿越条目；
    /// tar 符号链接条目还原为真符号链接（BtbN 的短名 soname 即此形态）。</summary>
    internal static void ExtractArchive(string archivePath, string targetDir)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // 未处理 zip 符号链接条目——2026-09-24 实测 BtbN win64 lgpl-shared zip 全部 238 条目
            // 均为实体文件（ExternalAttributes 高 16 位无 0xA1FF 链接编码；运行时 DLL 在 bin/ 下
            // 为数十 MB 实体）。若上游改为链接形态，Windows 侧会复刻 tar 摊平 bug，届时需在此补
            // ExternalAttributes 解析（.NET ZipArchive 不直接暴露链接目标，需自定义位解析）
            ZipFile.ExtractToDirectory(archivePath, targetDir, overwriteFiles: true);
            return;
        }

        using var stream = File.OpenRead(archivePath);
        using var reader = ReaderFactory.Open(stream);
        // 前缀比较必须带目录分隔符：targetDir=/data/ff 时裸前缀会放过 "../ffx/x" 条目
        // （解析到兄弟目录 /data/ffx），与 ManifestVerifier.ResolveSafe 同一防线
        var sandboxRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir))
                          + Path.DirectorySeparatorChar;
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(targetDir, reader.Entry.Key!));
            if (!fullPath.StartsWith(sandboxRoot, StringComparison.Ordinal))
            {
                throw new IOException($"Refusing entry outside target dir: {reader.Entry.Key}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            // 符号链接条目：BtbN tar 里短名 soname（libavcodec.so.63）是指向真实文件（.63.1.102）
            // 的符号链接，WriteEntryTo 会把它摊平成 0 字节普通文件（2026-09-24 实锤：本机下载目录
            // 14 个短名全 0 字节，短名永远加载失败）。仅允许同目录裸文件名目标——带分隔符/绝对路径/
            // ".." 的目标即逃逸，与条目沙箱同一拒绝语义
            var linkTarget = reader.Entry.LinkTarget;
            if (!string.IsNullOrEmpty(linkTarget))
            {
                if (linkTarget.Contains('/') || linkTarget.Contains('\\') || Path.IsPathRooted(linkTarget)
                    || linkTarget is ".." or ".")
                {
                    throw new IOException($"Refusing symlink with escaping target: {reader.Entry.Key} -> {linkTarget}");
                }

                if (Directory.Exists(fullPath))
                {
                    throw new IOException($"Symlink path occupied by a directory: {reader.Entry.Key}");
                }

                File.Delete(fullPath); // 清掉旧解压产物（含修复前摊平的 0 字节文件），幂等重解压
                File.CreateSymbolicLink(fullPath, linkTarget);
                continue;
            }

            reader.WriteEntryTo(fullPath);
        }
    }

    /// <summary>
    /// 目录预载的依赖序（readelf 实测 BtbN n9.0 构建，2026-09-24）：各库 DT_NEEDED 只有同伴
    /// soname（libavformat 需要 libavcodec.so.63、libavcodec 需要 libswresample.so.7/libavutil.so.61），
    /// 且这些库**没有 RUNPATH**——glibc 不会到被加载库自己的目录找依赖，只能靠"依赖先 dlopen、
    /// 按 soname 驻留"逐级满足；按 AutoGen 请求序（avformat 可能先于 avcodec）加载即必败。
    /// internal 供依赖序回归测试钉住相对位置（avutil 最先、avcodec 先于 avformat 等）。
    /// </summary>
    internal static readonly string[] LibraryDependencyOrder =
        ["avutil", "swresample", "swscale", "avcodec", "avformat", "avfilter", "avdevice"];

    /// <summary>目录是否携带完成标记（<see cref="CompletionMarkerFileName"/>）。</summary>
    private static bool HasCompletionMarker(string libraryDir) =>
        File.Exists(Path.Combine(libraryDir, CompletionMarkerFileName));

    /// <summary>递归查找目录里 avcodec 库文件所在目录；找不到返回 null。</summary>
    internal static string? LocateLibraryDir(string? root)
    {
        if (root is null || !Directory.Exists(root))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(p => AvcodecFileRegex().IsMatch(Path.GetFileName(p)))
                .Select(Path.GetDirectoryName)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>首运下载/解压目标根目录的默认值（按 RID 分目录，双平台互不干扰；internal 供路径策略直测）：
    /// 数据目录下的 ffmpeg——可重建大体积二进制归数据目录（2026-09-22 迁移，config 下旧库成遗留可手删）。
    /// 实例侧经构造参数 <c>downloadRoot</c> 注入覆盖（测试用），生产不传即用本默认。</summary>
    internal static string DefaultDownloadRoot
    {
        get
        {
            var rid = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "unknown";
            return Path.Combine(AppPaths.DataDirectory, "ffmpeg", rid);
        }
    }

    /// <summary>
    /// 把 FFmpeg.AutoGen 的库解析指向指定目录（优先），目录缺失该库时回退系统默认搜索。
    /// 实例在 ffmpeg 类型首次触碰前注入（绑定一次性），目录随后经 <see cref="SetDirectory"/> 设置；
    /// 库句柄缓存，解析失败按 throwOnError 语义抛 EntryPointNotFound。
    /// </summary>
    private sealed class DirectoryFunctionResolver : IFunctionResolver
    {
        private readonly Dictionary<string, IntPtr> _loaded = [];

        /// <summary>守护 <see cref="_loaded"/> 与预载标志：多个 transient 播放器的解码线程会并发
        /// 触发同一共享解析器实例的函数解析（M4，2026-09-24 review 立项——此前无锁并发访问普通
        /// Dictionary 属预存竞态）。锁为叶子级（持锁期间只做 dlopen/目录枚举，不与外层锁交互），
        /// 无死锁面；AutoGen 对每个函数只解析一次，锁竞争可忽略。并发竞态无确定性红测试（时序
        /// 依赖），按 VM-F4 同款"修复 + 声明"处理，不设专项竞态用例。</summary>
        private readonly object _gate = new();

        private string? _directory;

        /// <summary>目录预载是否已执行（见 <see cref="PreloadDirectoryLibraries"/>，每实例一次）。</summary>
        private bool _directoryPreloaded;

        /// <summary>全链解析执行次数（测试观测经外层 <see cref="FfmpegLibraryResolver.DirectoryResolutionAttemptsForTests"/>）。</summary>
        internal int ResolutionAttempts;

        /// <summary>设置库目录（null = 仅系统默认搜索）。</summary>
        public void SetDirectory(string? directory) => _directory = directory;

        public T GetFunctionDelegate<T>(string libraryName, string functionName, bool throwOnError)
        {
            var handle = GetOrLoadLibrary(libraryName);
            if (handle != IntPtr.Zero && NativeLibrary.TryGetExport(handle, functionName, out var address))
            {
                var del = Marshal.GetDelegateForFunctionPointer(address, typeof(T));
                return (T)(object)del;
            }

            if (throwOnError)
            {
                throw new EntryPointNotFoundException($"{libraryName}!{functionName}");
            }

            return (T)(object)null!;
        }

        /// <summary>按库名取句柄：负缓存直读（零句柄 = 全链已搜过且不可得，同样缓存——恢复
        /// "失败句柄也缓存避免反复尝试"的旧纪律，M3）；首入目录先按依赖序预载，表外库名再做
        /// 全链解析并缓存。必须持 <see cref="_gate"/>。</summary>
        private IntPtr GetOrLoadLibrary(string libraryName)
        {
            lock (_gate)
            {
                if (_loaded.TryGetValue(libraryName, out var cached))
                {
                    return cached;
                }

                if (!_directoryPreloaded && _directory is not null)
                {
                    PreloadDirectoryLibraries();
                    if (_loaded.TryGetValue(libraryName, out var preloaded))
                    {
                        return preloaded;
                    }
                }

                var handle = ResolveLibrary(libraryName);
                _loaded[libraryName] = handle;
                return handle;
            }
        }

        /// <summary>首入目录时按 <see cref="LibraryDependencyOrder"/> 预载全部库（调用方须持 <see cref="_gate"/>）：
        /// 目录内版本化文件互相以 soname 依赖且无 RUNPATH，glibc 只认"已驻留对象 + 系统搜索路径"，
        /// 依赖不先驻留则 avformat/avcodec 这类后位库 dlopen 必败（2026-09-24 本机实锤：
        /// 只加载出 libavutil，avformat_open_input 落到 AutoGen throw-stub 抛 NotSupportedException）。
        /// 预载经 <see cref="ResolveLibrary"/> 全链解析——结果（含零句柄）即终态，此后不再重扫。</summary>
        private void PreloadDirectoryLibraries()
        {
            _directoryPreloaded = true;
            foreach (var name in LibraryDependencyOrder)
            {
                if (!_loaded.ContainsKey(name))
                {
                    _loaded[name] = ResolveLibrary(name);
                }
            }
        }

        /// <summary>单库全链解析：目录内（裸名→版本化枚举）→ 系统精确主版本 → 系统裸名；
        /// 结果含零句柄（全链不可得，交由调用方负缓存）。必须持 <see cref="_gate"/>。</summary>
        private IntPtr ResolveLibrary(string libraryName)
        {
            ResolutionAttempts++;
            if (_directory is not null)
            {
                var fromDir = TryLoadFromDirectory(libraryName);
                if (fromDir != IntPtr.Zero)
                {
                    return fromDir;
                }
            }

            if (OperatingSystem.IsLinux())
            {
                // 发行版布局：lib<名>.so.<主版本>（精确配套）。无版本 lib<名>.so 符号链接绝不回退：
                // 它指向发行版当前系列（2026-09-24 本机实测 →.so.62，绑定要 63），av_version_info
                // 恰好 ABI 稳定让 TryBind 假成功，首个结构体调用处即崩——宁缺毋滥
                if (NativeLibrary.TryLoad($"lib{libraryName}.so.{LibraryFileMajor(libraryName)}", out var handle))
                {
                    return handle;
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                // Windows 系统库带主版本（avutil-61.dll）：裸名 avutil 永远解析不到版本化 DLL，
                // 精确匹配系统上同样会在 avcodec 预检命中后于 avutil 处必败（与 Linux 同根因）
                if (NativeLibrary.TryLoad($"{libraryName}-{LibraryFileMajor(libraryName)}.dll", out var handle))
                {
                    return handle;
                }
            }

            NativeLibrary.TryLoad(libraryName, out var fallback);
            return fallback;
        }

        /// <summary>目录内加载：先裸名文件（Windows 加 .dll），再版本化文件按前缀枚举（序号大的优先），
        /// 不触碰绑定版本表；失败返回零句柄（系统回退交给 <see cref="ResolveLibrary"/>）。必须持 <see cref="_gate"/>。</summary>
        private IntPtr TryLoadFromDirectory(string libraryName)
        {
            var directory = _directory!;
            var bare = OperatingSystem.IsWindows() ? libraryName + ".dll" : libraryName;
            var barePath = Path.Combine(directory, bare);
            if (File.Exists(barePath) && NativeLibrary.TryLoad(barePath, out var handle))
            {
                return handle;
            }

            // 版本化文件名（avcodec-63.dll / libavcodec.so.63）：目录内按前缀枚举，不依赖绑定版本表
            var versioned = Directory.EnumerateFiles(directory,
                    (OperatingSystem.IsWindows() ? libraryName + "-" : "lib" + libraryName + ".so.") + "*")
                .OrderByDescending(p => p, StringComparer.Ordinal);
            foreach (var candidate in versioned)
            {
                if (NativeLibrary.TryLoad(candidate, out handle))
                {
                    return handle;
                }
            }

            return IntPtr.Zero;
        }
    }
}
