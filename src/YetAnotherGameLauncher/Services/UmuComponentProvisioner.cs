using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Readers;
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

    private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

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

        // 1) 绝对路径且已就绪
        if (Path.IsPathRooted(protonRequest) && IsProtonReady(protonRequest))
        {
            return Path.GetFullPath(protonRequest);
        }

        // 2) compatibilitytools.d 下的版本名
        var byName = Path.Combine(UmuPaths.SteamCompatRoot(dataHome), protonRequest);
        if (IsProtonReady(byName))
        {
            return Path.GetFullPath(byName);
        }

        // 3) 本机已装的最新（代号 GE-Proton / UMU-Proton，或任意）
        var localLatest = FindLatestLocalProton(protonRequest);
        if (localLatest is not null && !IsCodename(protonRequest) && !IsVersionRequest(protonRequest))
        {
            return localLatest;
        }

        if (!IsCodename(protonRequest) && Directory.Exists(byName))
        {
            // 指定了版本名但目录残缺：强制重下
        }
        else if (localLatest is not null && IsCodename(protonRequest))
        {
            // 代号：若本地已有该前缀最新则仍尝试检查网络更新；失败则用本地
            try
            {
                return await DownloadLatestProtonAsync(protonRequest, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException)
            {
                logger?.LogWarning(ex, "Proton 更新失败，回退本地 {Path}", localLatest);
                return localLatest;
            }
        }

        // 4) 下载
        if (IsCodename(protonRequest) || IsVersionRequest(protonRequest) || !IsProtonReady(protonRequest))
        {
            progress?.Report($"正在准备 Proton（{protonRequest}）…");
            return await DownloadLatestProtonAsync(protonRequest, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        return Path.GetFullPath(protonRequest);
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
            var assets = document.RootElement.GetProperty("assets")
                .EnumerateArray()
                .Select(a => (
                    Name: a.GetProperty("name").GetString() ?? "",
                    Url: a.GetProperty("browser_download_url").GetString() ?? ""))
                .Where(a => a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                            && !a.Name.Contains("sha512", StringComparison.OrdinalIgnoreCase)
                            && !a.Name.Contains("sha256sum", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            asset = assets.FirstOrDefault();
            if (string.IsNullOrEmpty(asset.Url))
            {
                throw new UpdateException("Proton release 里没有 .tar.gz 资产。");
            }
        }
        catch (JsonException ex)
        {
            throw new LaunchException(
                LaunchFailureKind.ProtonDownloadFailed,
                $"Proton 版本信息解析失败：{ex.Message}",
                ex);
        }

        var compatRoot = UmuPaths.SteamCompatRoot(dataHome);
        Directory.CreateDirectory(compatRoot);
        var extractName = asset.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? asset.Name[..^".tar.gz".Length]
            : Path.GetFileNameWithoutExtension(asset.Name);
        var targetDir = Path.Combine(compatRoot, extractName);
        var tarPath = Path.Combine(
            UmuPaths.CacheRoot(cacheHome),
            asset.Name);

        Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);
        progress?.Report($"正在下载 {asset.Name}…");

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
            logger?.LogInformation("Installed Proton to {Dir}", targetDir);
            return Path.GetFullPath(targetDir);
        }
        catch (LaunchException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or ArchiveException or InvalidOperationException)
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
            ExtractRuntimeArchive(archivePath, staging, info.Name);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArchiveException)
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

    private static void ExtractRuntimeArchive(string archivePath, string staging, string codename)
    {
        using var stream = File.OpenRead(archivePath);
        using var reader = ReaderFactory.Open(stream);
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            // 路径穿越防护
            var key = reader.Entry.Key?.Replace('\\', '/') ?? "";
            if (key.StartsWith('/') || key.Contains("../", StringComparison.Ordinal))
            {
                continue;
            }

            reader.WriteEntryToDirectory(staging, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true,
            });
        }
    }

    private static void ExtractSingleTopLevel(string archivePath, string targetDir)
    {
        var temp = targetDir + ".extract";
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }

        Directory.CreateDirectory(temp);
        try
        {
            using var stream = File.OpenRead(archivePath);
            using var reader = ReaderFactory.Open(stream);
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory)
                {
                    continue;
                }

                var key = reader.Entry.Key?.Replace('\\', '/') ?? "";
                if (key.StartsWith('/') || key.Contains("../", StringComparison.Ordinal))
                {
                    continue;
                }

                reader.WriteEntryToDirectory(temp, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true,
                });
            }

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

    private string? FindLatestLocalProton(string prefixOrCodename)
    {
        var root = UmuPaths.SteamCompatRoot(dataHome);
        if (!Directory.Exists(root))
        {
            return null;
        }

        var filter = IsCodename(prefixOrCodename)
            ? prefixOrCodename
            : prefixOrCodename.StartsWith("GE", StringComparison.OrdinalIgnoreCase) ? "GE-Proton"
            : prefixOrCodename.StartsWith("UMU", StringComparison.OrdinalIgnoreCase) ? "UMU-Proton"
            : null;

        return Directory.EnumerateDirectories(root)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return IsProtonReady(d) &&
                       (filter is null || name.StartsWith(filter, StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath)
            .FirstOrDefault();
    }

    private static bool IsCodename(string value) =>
        string.Equals(value, "GE-Proton", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "UMU-Proton", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "GE-Latest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "UMU-Latest", StringComparison.OrdinalIgnoreCase);

    private static bool IsVersionRequest(string value) =>
        value.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("UMU-Proton", StringComparison.OrdinalIgnoreCase);

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
