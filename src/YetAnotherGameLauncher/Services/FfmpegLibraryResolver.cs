using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Readers;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Utilities;
using static FFmpeg.AutoGen.ffmpeg;

namespace YetAnotherGameLauncher.Services;

[ExcludeFromCodeCoverage]
/// <summary>
/// FFmpeg 原生库准备器（FFmpeg.AutoGen 绑定 ↔ 原生库的版本必须配套，且绑定初始化只有一次机会）：
/// ① 复用应用数据目录里已下载的库；② 探测系统已安装的同版本 FFmpeg（Linux 桌面发行版常见，命中即零下载）；
/// ③ 都没有时从 BtbN FFmpeg-Builds 下载与绑定版本配套的 LGPL 共享构建（SHA256 校验后解压）。
/// 就绪判定 = 通过自定义 <see cref="IFunctionResolver"/> 实际调通 FFmpeg 版本 API。
/// </summary>
public sealed partial class FfmpegLibraryResolver(
    YetAnotherGameLauncher.Core.Services.NetworkProxyManager proxyManager,
    ILogger<FfmpegLibraryResolver>? logger = null)
{
    /// <summary>大文件下载专用 client：共享底层 handler（代理设置同步生效），仅放宽超时。</summary>
    private readonly HttpClient _downloadClient = new(proxyManager.Handler)
    {
        Timeout = DownloadTimeout,
    };

    /// <summary>下载源：与 FFmpeg.AutoGen 9.0.x 绑定配套的 FFmpeg 9.0 LGPL 共享构建（双平台）。</summary>
    private const string BtbnBaseUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download";

    /// <summary>下载大文件（约 50MB）不能复用全局 30 秒超时的 HttpClient：专用慢速超时。</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    // 解析状态：0=未尝试（下载失败后停留于此，允许下次重试）/ 1=已就绪 / 2=永久失败（绑定已实际尝试，
    // ffmpeg 类型的静态初始化只有一次机会，失败后的函数委托是不可重试的 throw stub）
    private const int ResolveNotAttempted = 0;
    private const int ResolveReady = 1;
    private const int ResolveFailedPermanently = 2;

    /// <summary>解析状态（原子读写，见 <see cref="ResolveNotAttempted"/> 等常量）。</summary>
    private int _resolveState;

    /// <summary>解析器单例：必须在 ffmpeg 类型首次触碰前注入，目录随后设置。</summary>
    private DirectoryFunctionResolver? _sharedResolver;

    /// <summary>下载源：按平台给出 BtbN 资产文件名与校验行；非 Win/Linux 返回 null（无下载支持）。</summary>
    private static (string Asset, string Pattern)? BtbnAsset =>
        OperatingSystem.IsWindows()
            ? ("ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip", @"([0-9a-f]{64})[ \t]+\*?ffmpeg-n9\.0-latest-win64-lgpl-shared-9\.0\.zip")
            : OperatingSystem.IsLinux()
                ? ("ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0.tar.xz", @"([0-9a-f]{64})[ \t]+\*?ffmpeg-n9\.0-latest-linux64-lgpl-shared-9\.0\.tar\.xz")
                : null;

    /// <summary>avcodec 库文件名模式（判断目录里是否真的有库）。</summary>
    [GeneratedRegex(@"^(lib)?avcodec(-\d+)?(\.so(\.\d+)*)?(\.dll)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AvcodecFileRegex();

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

        // ffmpeg 类型的首次触碰（含读 LibraryVersionMap）会以"当时的 FunctionResolver"完成一次性绑定，
        // 因此解析器必须最先注入（共享实例、目录后置），此后才能触碰 ffmpeg 的任何成员
        _sharedResolver = new DirectoryFunctionResolver();
        DynamicallyLoadedBindings.FunctionResolver = _sharedResolver;

        // ① 已下载目录命中（自己校验过的完整库目录）
        var dir = LocateLibraryDir(DownloadRoot);
        if (dir is not null && TryBind(dir))
        {
            return true;
        }

        // ② 系统已装与绑定精确同版本的 FFmpeg：文件名预检（不触碰 ffmpeg 类型），命中即零下载
        if (SystemLibraryPresent())
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
            dir = LocateLibraryDir(DownloadRoot);
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
            _ = av_version_info();
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

    /// <summary>绑定所需的 libavcodec 主版本号（Linux 的 so 版本号，与 <see cref="BoundAvcodecFile"/> 同步改）。</summary>
    internal const int BoundLibavMajor = 63;

    /// <summary>下载 BtbN 资产（SHA256 校验）并解压到应用数据目录。</summary>
    private async Task DownloadAndExtract(CancellationToken cancellationToken)
    {
        var (asset, checksumPattern) = BtbnAsset!.Value;
        Directory.CreateDirectory(DownloadRoot);

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

            VerifySha256(tempFile, expected);
            ExtractArchive(tempFile, DownloadRoot);
            logger?.LogInformation("FFmpeg libraries installed to {Directory}", DownloadRoot);
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
    private static void VerifySha256(string path, string expectedSha256)
    {
        var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new System.Security.Cryptography.CryptographicException(
                $"FFmpeg package checksum mismatch: {actual} != {expectedSha256}");
        }
    }

    /// <summary>按扩展名解压（zip 用内置实现，tar.xz 用 SharpCompress），拒绝路径穿越条目。</summary>
    private static void ExtractArchive(string archivePath, string targetDir)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, targetDir, overwriteFiles: true);
            return;
        }

        using var stream = File.OpenRead(archivePath);
        using var reader = ReaderFactory.Open(stream);
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(targetDir, reader.Entry.Key!));
            if (!fullPath.StartsWith(Path.GetFullPath(targetDir), StringComparison.Ordinal))
            {
                throw new IOException($"Refusing entry outside target dir: {reader.Entry.Key}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            reader.WriteEntryTo(fullPath);
        }
    }

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

    /// <summary>首运下载/解压目标根目录（按 RID 分目录，双平台互不干扰）。</summary>
    private static string DownloadRoot
    {
        get
        {
            var rid = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "unknown";
            return Path.Combine(AppPaths.ConfigDirectory, "ffmpeg", rid);
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

        private string? _directory;

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

        /// <summary>按库名加载：AutoGen 传裸名（avcodec），Windows 实际文件带主版本（avcodec-63.dll）；
        /// 先目录内裸名文件，再目录内版本文件（按前缀枚举，不依赖绑定版本表），最后系统默认搜索；
        /// 失败句柄也缓存避免反复尝试。</summary>
        private IntPtr GetOrLoadLibrary(string libraryName)
        {
            if (_loaded.TryGetValue(libraryName, out var cached))
            {
                return cached;
            }

            var handle = IntPtr.Zero;
            var directory = _directory;
            if (directory is not null)
            {
                var bare = OperatingSystem.IsWindows() ? libraryName + ".dll" : libraryName;
                var barePath = Path.Combine(directory, bare);
                if (File.Exists(barePath) && NativeLibrary.TryLoad(barePath, out handle))
                {
                    _loaded[libraryName] = handle;
                    return handle;
                }

                // 版本化文件名（avcodec-63.dll / libavcodec.so.63）：目录内按前缀枚举，不触碰绑定版本表
                var versioned = Directory.EnumerateFiles(directory,
                        (OperatingSystem.IsWindows() ? libraryName + "-" : "lib" + libraryName + ".so.") + "*")
                    .OrderByDescending(p => p, StringComparer.Ordinal);
                foreach (var candidate in versioned)
                {
                    if (NativeLibrary.TryLoad(candidate, out handle))
                    {
                        break;
                    }

                    handle = IntPtr.Zero;
                }
            }

            if (handle == IntPtr.Zero)
            {
                if (OperatingSystem.IsLinux())
                {
                    // 发行版布局：lib<名>.so.<主版本>（精确配套）→ lib<名>.so（-dev 符号链接，
                    // 指向已装同系列版本）。裸 dlopen("avcodec") 在 Linux 永远失败——
                    // 既无 lib 前缀也无版本号，这是旧实现系统库探测失效的另一半原因
                    if (NativeLibrary.TryLoad($"lib{libraryName}.so.{BoundLibavMajor}", out handle)
                        || NativeLibrary.TryLoad($"lib{libraryName}.so", out handle))
                    {
                        _loaded[libraryName] = handle;
                        return handle;
                    }
                }

                NativeLibrary.TryLoad(libraryName, out handle);
            }

            _loaded[libraryName] = handle;
            return handle;
        }
    }
}
