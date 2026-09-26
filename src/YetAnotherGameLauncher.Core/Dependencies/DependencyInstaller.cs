using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Dependencies;

/// <summary>依赖安装器（供游戏设置页依赖区消费；测试替身也实现此接口）。</summary>
public interface IDependencyInstaller
{
    /// <summary>内置依赖目录（清单已过校验）。</summary>
    IReadOnlyList<DependencyManifest> Dependencies { get; }

    /// <summary>读取指定目标处某依赖的安装状态（完成标记存在且版本一致才算已安装）。</summary>
    DependencyInstallState GetState(WinePrefixTarget target, DependencyManifest manifest);

    /// <summary>安装依赖到目标 prefix：下载→解压→字体落位→注册表导入→完成标记。</summary>
    Task InstallAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        IProgress<DependencyProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 依赖安装编排器（Bottles cjkfonts 语义的落地形态）：
/// ① 前置校验（wine 可用、prefix 已初始化）→ ② 共享缓存下载（断点续传 + MD5/size 校验，
/// 多 prefix 复用）→ ③ 防御解压到暂存 → ④ 字体拷进 windows/Fonts → ⑤ 单次
/// <c>wine reg import</c> 写字体登记与替换（UTF-16LE .reg，WINEPREFIX 指向目标 prefix）→
/// ⑥ 原子写完成标记。任一阶段失败即中止且不写标记（幂等可重试）；取消透传 OCE。
/// </summary>
public sealed class DependencyInstaller(
    IDownloader downloader,
    IProcessRunner processRunner,
    IReadOnlyList<DependencyManifest>? dependencies = null,
    ILogger? logger = null,
    string? cacheRoot = null) : IDependencyInstaller
{
    /// <summary>注册表导入的超时：reg import 只是写注册表文件，秒级动作；超时视为 wineserver 异常。</summary>
    private const int RegistryTimeoutMilliseconds = 120_000;

    /// <summary>wine 首次触碰 prefix 时可能弹 mono/gecko 安装框——注册表导入场景一律禁用
    /// （Bottles/winecfg 脚本同款做法；prefix 已由游戏首启初始化，此处仅为纵深防御）。</summary>
    private const string SuppressMonoGeckoOverride = "mscoree,mshtml=";

    private readonly string _cacheRoot = cacheRoot ?? DependencyPaths.CacheRoot();

    /// <inheritdoc />
    public IReadOnlyList<DependencyManifest> Dependencies { get; } =
        dependencies ?? DependencyCatalog.LoadEmbedded();

    /// <inheritdoc />
    public DependencyInstallState GetState(WinePrefixTarget target, DependencyManifest manifest)
    {
        var markerPath = DependencyPaths.MarkerPath(target.StateDirectory, manifest.Id);
        if (!File.Exists(markerPath))
        {
            return new DependencyInstallState(false, null);
        }

        var content = File.ReadAllText(markerPath);
        var expected = MarkerContent(manifest);
        return string.Equals(content.Trim(), expected, StringComparison.Ordinal)
            ? new DependencyInstallState(true, manifest.Version)
            : new DependencyInstallState(false, null);
    }

    /// <inheritdoc />
    public async Task InstallAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        IProgress<DependencyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 前置校验先行：wine/prefix 缺失时绝不发起下载（90MB 级字体包，失败前置可省无效流量）
        if (!FileUtilities.IsExecutableFile(target.WineExecutable))
        {
            throw new DependencyException(
                DependencyFailureKind.WineMissing,
                $"wine 不可用：{target.WineExecutable}");
        }

        if (!Directory.Exists(Path.Combine(target.WinePrefixDirectory, "drive_c")))
        {
            throw new DependencyException(
                DependencyFailureKind.PrefixMissing,
                $"Wine prefix 未初始化（缺 drive_c）：{target.WinePrefixDirectory}。请先启动一次游戏。");
        }

        if (GetState(target, manifest) is { Installed: true })
        {
            logger?.LogInformation("Dependency {Id}@{Version} already installed, skipping", manifest.Id, manifest.Version);
            return;
        }

        var archivePath = await EnsureArchiveAsync(manifest, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new DependencyProgress(DependencyPhase.Extracting, null));
        var fontFiles = ExtractFonts(manifest, archivePath);
        try
        {
            progress?.Report(new DependencyProgress(DependencyPhase.Copying, null));
            CopyFonts(target, manifest, fontFiles);

            progress?.Report(new DependencyProgress(DependencyPhase.Registering, null));
            await ImportRegistryAsync(target, manifest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // 暂存无论成败用后即清（zip 解压产物可达百 MB 级）
            FileUtilities.TryDeleteDirectory(
                DependencyPaths.StagingDirectory(_cacheRoot, manifest.Id), logger);
        }

        await WriteMarkerAsync(target, manifest).ConfigureAwait(false);
        progress?.Report(new DependencyProgress(DependencyPhase.Done, null));
        logger?.LogInformation("Dependency {Id}@{Version} installed into {Prefix}", manifest.Id, manifest.Version, target.WinePrefixDirectory);
    }

    /// <summary>确保压缩包在共享缓存且校验通过：有效缓存直接复用，否则下载（DownloadRequest 自带
    /// 断点续传与 MD5/size 校验）。校验失败由下载器抛出，这里统一折算 DownloadFailed。</summary>
    private async Task<string> EnsureArchiveAsync(
        DependencyManifest manifest,
        IProgress<DependencyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var archivePath = Path.Combine(_cacheRoot, manifest.FileName);
        if (!IsCachedArchiveValid(manifest, archivePath))
        {
            progress?.Report(new DependencyProgress(DependencyPhase.Downloading, 0));
            var fraction = new FractionProgress(manifest.SizeBytes, progress);
            try
            {
                await downloader.DownloadFileAsync(
                    new DownloadRequest(manifest.DownloadUrl, archivePath, manifest.SizeBytes, manifest.Md5),
                    fraction,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DownloadException or InvalidOperationException or IOException)
            {
                throw new DependencyException(
                    DependencyFailureKind.DownloadFailed,
                    $"依赖压缩包下载失败：{manifest.DownloadUrl}：{ex.Message}", ex);
            }
        }

        return archivePath;
    }

    /// <summary>缓存有效性：尺寸与 MD5 都一致才复用（与清单校验语义一致：字段缺失不跳过校验，
    /// 内嵌清单已保证两字段恒在）。</summary>
    private static bool IsCachedArchiveValid(DependencyManifest manifest, string archivePath)
    {
        if (!File.Exists(archivePath) || new FileInfo(archivePath).Length != manifest.SizeBytes)
        {
            return false;
        }

        return Hashing.Md5Hex(archivePath).Equals(manifest.Md5, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>解压到暂存目录并返回清单声明的字体文件绝对路径；任一文件缺失按 ExtractFailed 失败
    /// （zip 解包的穿越/链接/炸弹防御复用 PackageInstallerService 的既有实现，单一事实源）。</summary>
    private List<string> ExtractFonts(DependencyManifest manifest, string archivePath)
    {
        var staging = DependencyPaths.StagingDirectory(_cacheRoot, manifest.Id);
        FileUtilities.TryDeleteDirectory(staging, logger);
        Directory.CreateDirectory(staging);
        try
        {
            PackageInstallerService.ExtractArchive(archivePath, staging, manifest.FileName);
        }
        catch (UpdateException ex)
        {
            throw new DependencyException(
                DependencyFailureKind.ExtractFailed,
                $"依赖压缩包解压失败：{manifest.FileName}：{ex.Message}", ex);
        }

        var files = new List<string>(manifest.Fonts.Count);
        foreach (var font in manifest.Fonts)
        {
            var path = Path.Combine(staging, font.File);
            if (!File.Exists(path))
            {
                throw new DependencyException(
                    DependencyFailureKind.ExtractFailed,
                    $"压缩包内缺少清单声明的字体文件：{font.File}");
            }

            files.Add(path);
        }

        return files;
    }

    /// <summary>字体拷进 windows/Fonts（覆盖写：同版本重装幂等）。</summary>
    private static void CopyFonts(WinePrefixTarget target, DependencyManifest manifest, List<string> stagedFiles)
    {
        try
        {
            Directory.CreateDirectory(target.FontsDirectory);
            foreach (var (font, staged) in manifest.Fonts.Zip(stagedFiles))
            {
                File.Copy(staged, Path.Combine(target.FontsDirectory, font.File), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DependencyException(
                DependencyFailureKind.FontCopyFailed,
                $"字体写入 prefix 失败：{ex.Message}", ex);
        }
    }

    /// <summary>生成 .reg（UTF-16LE）并单次 <c>wine reg import</c> 导入；退出码非零按 RegistryFailed
    /// 失败（附 stderr），临时 .reg 无论成败用后即删。</summary>
    private async Task ImportRegistryAsync(
        WinePrefixTarget target,
        DependencyManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target.StateDirectory);
        var regPath = Path.Combine(target.StateDirectory, $"{manifest.Id}.reg");
        try
        {
            await WineRegistryScriptBuilder.WriteAsync(
                regPath, WineRegistryScriptBuilder.Build(manifest), cancellationToken).ConfigureAwait(false);

            ProcessResult result;
            try
            {
                result = await processRunner.RunAsync(new ProcessStartSpec(
                    target.WineExecutable,
                    $"reg import \"{regPath}\"",
                    Environment: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["WINEPREFIX"] = target.WinePrefixDirectory,
                        ["WINEDLLOVERRIDES"] = SuppressMonoGeckoOverride,
                    },
                    TimeoutMilliseconds: RegistryTimeoutMilliseconds), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new DependencyException(
                    DependencyFailureKind.RegistryFailed,
                    $"wine reg import 启动失败：{ex.Message}", ex);
            }

            if (!result.Succeeded)
            {
                throw new DependencyException(
                    DependencyFailureKind.RegistryFailed,
                    $"wine reg import 退出码 {result.ExitCode}：{result.StandardError}");
            }
        }
        finally
        {
            FileUtilities.DeleteQuiet(regPath);
        }
    }

    /// <summary>原子写完成标记（内容 id@version；版本变更时可检测重装）。</summary>
    private static Task WriteMarkerAsync(WinePrefixTarget target, DependencyManifest manifest) =>
        FileUtilities.WriteAtomicAsync(
            DependencyPaths.MarkerPath(target.StateDirectory, manifest.Id),
            MarkerContent(manifest));

    private static string MarkerContent(DependencyManifest manifest) => $"{manifest.Id}@{manifest.Version}";

    /// <summary>把下载器报的绝对字节数折算成 [0,1] 进度。</summary>
    private sealed class FractionProgress(long totalBytes, IProgress<DependencyProgress>? progress) : IProgress<long>
    {
        public void Report(long value)
        {
            if (totalBytes > 0)
            {
                progress?.Report(new DependencyProgress(
                    DependencyPhase.Downloading,
                    Math.Clamp((double)value / totalBytes, 0, 1)));
            }
        }
    }
}
