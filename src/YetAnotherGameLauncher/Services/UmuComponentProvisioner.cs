using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Compressors.Xz;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services.Umu;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 原生 umu 兼容组件准备器：解析/下载 GE-Proton 或 UMU-Proton 到 compatibilitytools.d，
/// 下载 Steam Linux Runtime 到 ~/.local/share/umu/&lt;variant&gt;。纯 C#，不依赖 Python umu-run。
/// </summary>
public sealed class UmuComponentProvisioner(
    HttpClient httpClient,
    IDownloader downloader,
    ILogger? logger = null,
    string? dataHome = null,
    string? cacheHome = null) : IUmuComponentProvisioner
{
    /// <summary>GE-Proton 最新 release 的 GitHub API。</summary>
    public const string GeProtonReleaseApi =
        "https://api.github.com/repos/GloriousEggroll/proton-ge-custom/releases/latest";

    /// <summary>UMU-Proton 最新 release 的 GitHub API。</summary>
    public const string UmuProtonReleaseApi =
        "https://api.github.com/repos/Open-Wine-Components/UMU-Proton/releases/latest";

    /// <summary>Steam Runtime 镜像主机。</summary>
    public const string RuntimeHost = "https://repo.steampowered.com";

    /// <inheritdoc />
    public bool IsProtonReady(string protonPath)
    {
        if (string.IsNullOrWhiteSpace(protonPath) || !Directory.Exists(protonPath))
        {
            return false;
        }

        return File.Exists(Path.Combine(protonPath, "toolmanifest.vdf"))
               && File.Exists(Path.Combine(protonPath, "proton"));
    }

    /// <inheritdoc />
    public bool IsRuntimeReady(string runtimeVariant)
    {
        if (string.IsNullOrWhiteSpace(runtimeVariant))
        {
            return true;
        }

        var dir = UmuPaths.RuntimeDirectory(runtimeVariant, dataHome);
        return Directory.Exists(dir)
               && File.Exists(Path.Combine(dir, UmuPaths.InstallMarkerName))
               && (File.Exists(Path.Combine(dir, "_v2-entry-point")) || File.Exists(Path.Combine(dir, "umu")));
    }

    /// <inheritdoc />
    public async Task<string> EnsureProtonAsync(
        string protonRequest,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(protonRequest))
        {
            protonRequest = "UMU-Proton";
        }

        var ready = FindInstalledProtonPath(protonRequest);

        // 1) 绝对路径 / compatibilitytools.d 下的具体版本已就绪
        if (ready is not null && !IsCodename(protonRequest))
        {
            return ready;
        }

        // 2) 代号：本地最新作离线回退；下载最新失败才回退，不得静默换成其它 Proton
        if (ready is not null)
        {
            try
            {
                return await DownloadLatestProtonAsync(protonRequest, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or LaunchException)
            {
                logger?.LogWarning(ex, "Proton 更新失败，回退本地 {Path}", ready);
                return ready;
            }
        }

        // 3) 具体版本名只下该 tag；代号走 latest
        progress?.Report($"正在准备 Proton（{protonRequest}）…");
        if (IsCodename(protonRequest))
        {
            return await DownloadLatestProtonAsync(protonRequest, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        return await DownloadProtonByTagAsync(protonRequest, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public (string Variant, string Name)? ResolveRequiredRuntime(string protonRequest)
    {
        var path = FindInstalledProtonPath(protonRequest);
        if (path is null)
        {
            return null;
        }

        try
        {
            var runtime = ToolManifest.Load(path).RequiredRuntime;
            if (runtime.Name == "host" || string.IsNullOrEmpty(runtime.Variant))
            {
                return null;
            }

            return (runtime.Variant, runtime.Name);
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
        {
            // 清单不可读：调用方回退默认 Runtime
            return null;
        }
    }

    /// <summary>按与 EnsureProtonAsync 相同的规则做纯本地解析（不下载）：绝对路径、compatibilitytools.d 版本名、代号前缀最新。</summary>
    private string? FindInstalledProtonPath(string protonRequest)
    {
        if (string.IsNullOrWhiteSpace(protonRequest))
        {
            protonRequest = "UMU-Proton";
        }

        if (Path.IsPathRooted(protonRequest) && IsProtonReady(protonRequest))
        {
            return Path.GetFullPath(protonRequest);
        }

        var byName = Path.Combine(UmuPaths.SteamCompatRoot(dataHome), protonRequest);
        if (IsProtonReady(byName))
        {
            return Path.GetFullPath(byName);
        }

        return IsCodename(protonRequest) ? FindLatestLocalProton(protonRequest) : null;
    }

    /// <inheritdoc />
    public async Task EnsureRuntimeAsync(
        string runtimeVariant,
        string runtimeName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runtimeVariant) || IsRuntimeReady(runtimeVariant))
        {
            return;
        }

        var lockPath = UmuPaths.LockFile($"runtime-{runtimeVariant}.lock");
        using var _ = UmuPrefix.AcquireLock(lockPath);

        if (IsRuntimeReady(runtimeVariant))
        {
            return;
        }

        progress?.Report($"正在下载 Steam Runtime（{runtimeVariant}）…");
        await DownloadRuntimeAsync(runtimeVariant, runtimeName, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>按 release tag 下载指定版本 Proton；找不到该 tag 时抛可操作错误（禁止静默换成最新）。</summary>
    private async Task<string> DownloadProtonByTagAsync(
        string tagName,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var repo = tagName.StartsWith("UMU", StringComparison.OrdinalIgnoreCase)
            ? "Open-Wine-Components/UMU-Proton"
            : "GloriousEggroll/proton-ge-custom";
        var apiUrl = $"https://api.github.com/repos/{repo}/releases/tags/{tagName}";

        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.UserAgent.ParseAdd("YetAnotherGameLauncher");
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new LaunchException(
                    LaunchFailureKind.ProtonDownloadFailed,
                    $"找不到 Proton 版本「{tagName}」的 release。请改用代号（UMU-Proton/GE-Proton）或本机已装版本。");
            }

            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LaunchException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                $"无法获取 Proton {tagName} 的版本信息：{ex.Message}",
                ex);
        }

        (string Name, string Url) asset;
        try
        {
            using var document = JsonDocument.Parse(json);
            asset = SelectTarGzAsset(document, requiredPrefix: null);
            if (string.IsNullOrEmpty(asset.Url))
            {
                throw new LaunchException(
                    LaunchFailureKind.ProtonDownloadFailed,
                    $"Proton {tagName} 的 release 里没有 .tar.gz 资产。");
            }
        }
        catch (LaunchException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                $"Proton {tagName} 版本信息解析失败：{ex.Message}",
                ex);
        }

        progress?.Report($"正在下载 {asset.Name}…");
        var (targetDir, tarPath) = ResolveProtonInstallPaths(asset.Name);
        return await InstallProtonAssetAsync(asset, targetDir, tarPath, tagName, cancellationToken);
    }

    /// <summary>按代号（UMU-Proton / GE-Proton）下载对应仓库的最新构建；返回 Proton 绝对目录。</summary>
    private async Task<string> DownloadLatestProtonAsync(
        string protonRequest,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var apiUrl = protonRequest.StartsWith("UMU", StringComparison.OrdinalIgnoreCase)
            ? UmuProtonReleaseApi
            : GeProtonReleaseApi;

        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.UserAgent.ParseAdd("YetAnotherGameLauncher");
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                $"无法获取 Proton 版本信息（请检查网络或代理）：{ex.Message}",
                ex);
        }

        (string Name, string Url) asset;
        try
        {
            using var document = JsonDocument.Parse(json);
            var prefix = protonRequest.StartsWith("UMU", StringComparison.OrdinalIgnoreCase)
                ? "UMU-Proton"
                : "GE-Proton";
            asset = SelectTarGzAsset(document, prefix);
            if (string.IsNullOrEmpty(asset.Url))
            {
                throw new UpdateException($"Proton release 里没有匹配 {prefix}*.tar.gz 的资产。");
            }
        }
        catch (JsonException ex)
        {
            throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                $"Proton 版本信息解析失败：{ex.Message}",
                ex);
        }

        progress?.Report($"正在下载 {asset.Name}…");
        var (targetDir, tarPath) = ResolveProtonInstallPaths(asset.Name);
        return await InstallProtonAssetAsync(asset, targetDir, tarPath, tagName: null, cancellationToken);
    }

    /// <summary>从 release 资产里选第一个可用的 .tar.gz（排除校验文件）；requiredPrefix 非空时限定文件名前缀。</summary>
    private static (string Name, string Url) SelectTarGzAsset(JsonDocument document, string? requiredPrefix)
    {
        return document.RootElement.GetProperty("assets")
            .EnumerateArray()
            .Select(a => (
                Name: a.GetProperty("name").GetString() ?? "",
                Url: a.GetProperty("browser_download_url").GetString() ?? ""))
            .Where(a => a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                        && (requiredPrefix is null
                            || a.Name.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
                        && !a.Name.Contains("sha512", StringComparison.OrdinalIgnoreCase)
                        && !a.Name.Contains("sha256sum", StringComparison.OrdinalIgnoreCase))
            .ToArray()
            .FirstOrDefault();
    }

    /// <summary>计算 Proton 压缩包的解压目标目录（compatibilitytools.d 下）与缓存归档路径，并建好所需目录。</summary>
    private (string TargetDir, string TarPath) ResolveProtonInstallPaths(string assetName)
    {
        var compatRoot = UmuPaths.SteamCompatRoot(dataHome);
        Directory.CreateDirectory(compatRoot);
        var targetDir = Path.Combine(compatRoot, ProtonExtractDirectoryName(assetName));
        var tarPath = Path.Combine(UmuPaths.CacheRoot(cacheHome), assetName);
        Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);
        return (targetDir, tarPath);
    }

    /// <summary>去掉 .tar.gz 扩展名作为解压目录名；其余扩展名仅去最后一段扩展。</summary>
    private static string ProtonExtractDirectoryName(string assetName) =>
        assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? assetName[..^".tar.gz".Length]
            : Path.GetFileNameWithoutExtension(assetName);

    /// <summary>
    /// proton.lock 互斥下的落位流程：已就绪直接返回；否则下载归档 → 解包 → 校验目录布局 → 补执行位，
    /// 归档无论成败用后即删。tagName 仅用于日志（按 tag 下载时记录版本名）。返回 Proton 绝对目录。
    /// </summary>
    private async Task<string> InstallProtonAssetAsync(
        (string Name, string Url) asset,
        string targetDir,
        string tarPath,
        string? tagName,
        CancellationToken cancellationToken)
    {
        using (UmuPrefix.AcquireLock(UmuPaths.LockFile("proton.lock")))
        {
            if (IsProtonReady(targetDir))
            {
                return Path.GetFullPath(targetDir);
            }

            try
            {
                await downloader.DownloadFileAsync(
                    new DownloadRequest(asset.Url, tarPath, ExpectedSize: null, ExpectedMd5: null),
                    null,
                    cancellationToken).ConfigureAwait(false);

                // release 资产可能是 .tar.gz；SharpCompress 可直接读 gzip+tar
                ExtractSingleTopLevel(tarPath, targetDir);

                if (!IsProtonReady(targetDir))
                {
                    throw new LaunchException(
                        LaunchFailureKind.ProtonDownloadFailed,
                        $"Proton 包「{asset.Name}」解压后缺少 proton 或 toolmanifest.vdf。");
                }

                TryChmod(Path.Combine(targetDir, "proton"));
                if (tagName is null)
                {
                    logger?.LogInformation("Installed Proton to {Dir}", targetDir);
                }
                else
                {
                    logger?.LogInformation("Installed Proton {Tag} to {Dir}", tagName, targetDir);
                }

                return Path.GetFullPath(targetDir);
            }
            catch (LaunchException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                or ArchiveException or InvalidDataException or FormatException
                or InvalidOperationException)
            {
                throw new LaunchException(
                    LaunchFailureKind.ProtonDownloadFailed,
                    $"Proton 下载或解压失败：{ex.Message}",
                    ex);
            }
            finally
            {
                TryDelete(tarPath);
            }
        }
    }

    /// <summary>从官方镜像下载指定变体的 Steam Runtime：解析版本号 → SHA256SUMS/BUILD_ID → 下载校验 →
    /// 暂存目录解包 → 原子落位并写安装标记。</summary>
    private async Task DownloadRuntimeAsync(
        string variant,
        string runtimeName,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var info = SteamRuntimeCatalog.ByAppId.Values
            .FirstOrDefault(r => string.Equals(r.Variant, variant, StringComparison.Ordinal))
            ?? new SteamRuntimeInfo(runtimeName, variant, "", "x86_64");

        var images = SteamRuntimeCatalog.ImagesPathPrefix(info);
        var version = await FetchTextAsync(
            $"{RuntimeHost}{images}/latest-public-beta.txt", cancellationToken).ConfigureAwait(false);
        version = version.Trim();
        if (version.Length == 0)
        {
            throw new LaunchException(
                LaunchFailureKind.UmuRuntimeDownloadFailed,
                "无法解析 Steam Runtime 版本号（latest-public-beta.txt 为空）。");
        }

        var archive = SteamRuntimeCatalog.ArchiveFileName(info);
        var baseUrl = $"{RuntimeHost}{images}/{version}";
        var sums = await FetchTextAsync($"{baseUrl}/SHA256SUMS", cancellationToken).ConfigureAwait(false);
        var expectedSha = ParseSha256For(sums, archive);
        var buildId = (await FetchTextAsync($"{baseUrl}/BUILD_ID.txt", cancellationToken).ConfigureAwait(false)).Trim();

        var cache = UmuPaths.CacheRoot(cacheHome);
        Directory.CreateDirectory(cache);
        var archivePath = Path.Combine(cache, $"{archive}.{buildId}");
        progress?.Report($"正在下载 Steam Runtime {version}…");

        await downloader.DownloadFileAsync(
            new DownloadRequest($"{baseUrl}/{archive}", archivePath, ExpectedSize: null, ExpectedMd5: null),
            null,
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(expectedSha))
        {
            await VerifySha256Async(archivePath, expectedSha, cancellationToken).ConfigureAwait(false);
        }

        var installRoot = UmuPaths.RuntimeDirectory(variant, dataHome);
        var staging = installRoot + ".staging";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        try
        {
            ExtractTarArchive(archivePath, staging);
            // tar 内顶层目录 SteamLinuxRuntime_* → 挪到 installRoot
            var top = Directory.EnumerateDirectories(staging).FirstOrDefault()
                      ?? throw new LaunchException(
                          LaunchFailureKind.UmuRuntimeDownloadFailed,
                          "Steam Runtime 包内没有顶层目录。");
            if (Directory.Exists(installRoot))
            {
                Directory.Delete(installRoot, recursive: true);
            }

            Directory.Move(top, installRoot);
            File.WriteAllText(Path.Combine(installRoot, UmuPaths.InstallMarkerName), DateTime.UtcNow.ToString("O"));

            var entry = Path.Combine(installRoot, "_v2-entry-point");
            if (File.Exists(entry))
            {
                TryChmod(entry);
                var umuLink = Path.Combine(installRoot, "umu");
                if (!File.Exists(umuLink) && !Directory.Exists(umuLink))
                {
                    try
                    {
                        File.CreateSymbolicLink(umuLink, "_v2-entry-point");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        // 忽略
                    }
                }
            }

            logger?.LogInformation("Installed Steam Runtime {Variant} to {Dir}", variant, installRoot);
        }
        catch (LaunchException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArchiveException
            or InvalidDataException or FormatException or InvalidOperationException)
        {
            throw new LaunchException(
                LaunchFailureKind.UmuRuntimeDownloadFailed,
                $"Steam Runtime 解压或落位失败：{ex.Message}",
                ex);
        }
        finally
        {
            TryDelete(archivePath);
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>
    /// 解包 tar / tar.gz / tar.xz 到目标目录。条目解析与落盘走 System.Formats.Tar，
    /// 按 tar 头还原 Unix 权限位（SharpCompress 的解压不保留执行位，Proton/Runtime 树离开执行位无法启动）；
    /// 符号链接按原样创建，硬链接条目无数据、目标已解出时退化为符号链接。失败条目静默跳过（同 tar -x 容错），
    /// 拒绝绝对路径与 ".." 穿越。xz 容器仍用 SharpCompress 的 XZStream（BCL 无 XZ 解码）。
    /// </summary>
    internal static void ExtractTarArchive(string archivePath, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        using var raw = File.OpenRead(archivePath);
        using var decompressed = OpenDecompressedStream(raw);
        using var reader = new TarReader(decompressed);
        while (reader.GetNextEntry() is { } entry)
        {
            var key = entry.Name.Replace('\\', '/');
            // 路径穿越防护
            if (key.StartsWith('/') || key.Split('/').Contains(".."))
            {
                continue;
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(Path.Combine(destinationDir, key));
                    break;
                case TarEntryType.RegularFile:
                    WriteRegularFile(destinationDir, entry, key);
                    break;
                case TarEntryType.SymbolicLink:
                case TarEntryType.HardLink:
                    TryCreateLink(Path.Combine(destinationDir, key), entry.LinkName, entry.EntryType);
                    break;
            }
        }
    }

    /// <summary>按魔数选择解压流：gzip → GZipStream，xz → XZStream，其余按未压缩 tar 原样透传。</summary>
    private static Stream OpenDecompressedStream(FileStream raw)
    {
        Span<byte> magic = stackalloc byte[6];
        var filled = 0;
        while (filled < magic.Length)
        {
            var read = raw.Read(magic[filled..]);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        raw.Position = 0;
        if (filled >= 2 && magic[0] == 0x1F && magic[1] == 0x8B)
        {
            return new GZipStream(raw, CompressionMode.Decompress);
        }

        if (filled >= 6 && magic[0] == 0xFD && magic[1] == 0x37 && magic[2] == 0x7A
            && magic[3] == 0x58 && magic[4] == 0x5A && magic[5] == 0x00)
        {
            return new XZStream(raw);
        }

        return raw;
    }

    /// <summary>落盘普通文件：tar 头权限位经 UnixCreateMode 原样还原（含执行位）；无权限信息时走 umask 默认。</summary>
    private static void WriteRegularFile(string destinationDir, TarEntry entry, string key)
    {
        var target = Path.Combine(destinationDir, key);
        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows() && entry.Mode != UnixFileMode.None)
        {
            options.UnixCreateMode = entry.Mode & AllPermissionBits;
        }

        using var output = new FileStream(target, options);
        entry.DataStream?.CopyTo(output);
    }

    /// <summary>
    /// 创建链接条目：符号链接按 tar 记录的原样重建；硬链接条目没有数据段，
    /// 目标已解出时以符号链接替代（同一份内容，保证可执行语义），否则跳过。
    /// 无链接权限（如 Windows 未开开发者模式）时静默跳过，不阻断整个解包。
    /// </summary>
    private static void TryCreateLink(string linkPath, string targetName, TarEntryType entryType)
    {
        var parent = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }

            if (entryType == TarEntryType.HardLink)
            {
                var linkDir = Path.GetDirectoryName(linkPath);
                var resolved = string.IsNullOrEmpty(linkDir) ? targetName : Path.Combine(linkDir, targetName);
                if (!File.Exists(resolved))
                {
                    return;
                }
            }

            File.CreateSymbolicLink(linkPath, targetName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

    /// <summary>tar 头里可用的九个权限位。</summary>
    private const UnixFileMode AllPermissionBits =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>internal 供单测（经 InternalsVisibleTo）：验证顶层目录迁移与“无顶层目录直接摊开”两种包布局。</summary>
    internal static void ExtractSingleTopLevel(string archivePath, string targetDir)
    {
        var temp = targetDir + ".extract";
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }

        Directory.CreateDirectory(temp);
        try
        {
            ExtractTarArchive(archivePath, temp);

            // GE-Proton 包顶层通常是一个目录；也可能直接摊开
            var top = Directory.EnumerateDirectories(temp).FirstOrDefault();
            var source = top ?? temp;
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, recursive: true);
            }

            Directory.Move(source, targetDir);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                try
                {
                    Directory.Delete(temp, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>在 compatibilitytools.d 下找某代号前缀已装的最新版本（数字段自然序）；没有返回 null。</summary>
    private string? FindLatestLocalProton(string prefixOrCodename)
    {
        var root = UmuPaths.SteamCompatRoot(dataHome);
        if (!Directory.Exists(root))
        {
            return null;
        }

        string? filter;
        if (prefixOrCodename.StartsWith("UMU", StringComparison.OrdinalIgnoreCase))
        {
            filter = "UMU-Proton";
        }
        else if (prefixOrCodename.StartsWith("GE", StringComparison.OrdinalIgnoreCase))
        {
            filter = "GE-Proton";
        }
        else
        {
            return null;
        }

        // 数字段自然序：GE-Proton10-* 应排在 GE-Proton9-* 之前
        return Directory.EnumerateDirectories(root)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return IsProtonReady(d) && name.StartsWith(filter, StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(
                d => string.Concat(System.Text.RegularExpressions.Regex
                    .Matches(Path.GetFileName(d), @"\d+").Select(m => m.Value.PadLeft(6, '0'))),
                StringComparer.Ordinal)
            .Select(Path.GetFullPath)
            .FirstOrDefault();
    }

    /// <summary>是否为代号（GE-Proton / UMU-Proton / GE-Latest / UMU-Latest，忽略大小写）；启动设置卡共用此判定。</summary>
    internal static bool IsCodename(string value) =>
        string.Equals(value, "GE-Proton", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "UMU-Proton", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "GE-Latest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "UMU-Latest", StringComparison.OrdinalIgnoreCase);

    /// <summary>GET 一个文本响应（带 User-Agent）；非 2xx 由 EnsureSuccessStatusCode 抛 HttpRequestException。</summary>
    private async Task<string> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("YetAnotherGameLauncher");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>从 SHA256SUMS 文本里解析目标文件名的摘要。</summary>
    public static string ParseSha256For(string sumsText, string fileName)
    {
        foreach (var line in sumsText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.EndsWith(fileName, StringComparison.Ordinal))
            {
                return trimmed.Split(' ', '\t')[0].Trim();
            }
        }

        return string.Empty;
    }

    private static async Task VerifySha256Async(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, expected.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new LaunchException(
                LaunchFailureKind.UmuRuntimeDownloadFailed,
                $"Steam Runtime 校验失败：期望 SHA256 {expected}，实际 {actual}。");
        }
    }

    private static void TryChmod(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
