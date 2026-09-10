using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Readers;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// umu-launcher 引导安装器：从 GitHub release 元数据挑出 zipapp 资产（umu-launcher-*-zipapp.tar），
/// 经 IDownloader 下载后解出单文件 umu-run 落到应用数据目录并补可执行位。
/// 装完后 CompatTools.FindUmuRun 会扫到它，推荐链无需任何额外配置。
/// </summary>
public sealed class UmuLauncherInstaller(HttpClient httpClient, IDownloader downloader, ILogger? logger = null)
{
    /// <summary>umu-launcher 最新版 release 元数据地址（GitHub API）。</summary>
    public const string ReleaseApiUrl =
        "https://api.github.com/repos/Open-Wine-Components/umu-launcher/releases/latest";

    /// <summary>
    /// 安装最新版 umu-launcher 到 <paramref name="installDirectory"/>（umu-run 直接放在该目录下）。
    /// 返回 umu-run 的完整路径；任何一步失败抛带可操作信息的 <see cref="UpdateException"/>。
    /// </summary>
    public async Task<string> InstallLatestAsync(
        string installDirectory, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var assetUrl = await ResolveZipappAssetUrlAsync(cancellationToken).ConfigureAwait(false);
        if (assetUrl is null)
        {
            throw new UpdateException("umu-launcher 最新版没有提供 zipapp 安装包，请到项目页手动安装。");
        }

        Directory.CreateDirectory(installDirectory);
        var tarPath = Path.Combine(installDirectory, "umu-launcher.zipapp.tar");
        try
        {
            await downloader.DownloadFileAsync(
                new DownloadRequest(assetUrl, tarPath, ExpectedSize: null, ExpectedMd5: null),
                progress,
                cancellationToken).ConfigureAwait(false);
            return ExtractUmuRun(tarPath, installDirectory);
        }
        catch (Exception ex) when (ex is not UpdateException
            && ex is HttpRequestException or IOException or UnauthorizedAccessException
                or SharpCompress.Common.ArchiveException or InvalidOperationException)
        {
            logger?.LogWarning(ex, "umu-launcher 安装失败");
            throw new UpdateException($"umu-launcher 下载或解压失败：{ex.Message}");
        }
        finally
        {
            TryDelete(tarPath);
        }
    }

    /// <summary>从 release 资产列表里挑 zipapp 安装包的下载地址；没有返回 null。</summary>
    public static string? SelectZipappAssetUrl(IEnumerable<(string Name, string Url)> assets) =>
        assets.FirstOrDefault(a =>
                a.Name.StartsWith("umu-launcher-", StringComparison.Ordinal)
                && a.Name.EndsWith("-zipapp.tar", StringComparison.Ordinal))
            .Url;

    /// <summary>请求 GitHub API 解析最新版 zipapp 资产地址；网络/解析失败抛 UpdateException。</summary>
    private async Task<string?> ResolveZipappAssetUrlAsync(CancellationToken cancellationToken)
    {
        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
            // GitHub API 强制要求 User-Agent，缺省会 403
            request.Headers.UserAgent.ParseAdd("YetAnotherGameLauncher");
            using var response = await httpClient
                .SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            json = await response.Content
                .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger?.LogWarning(ex, "获取 umu-launcher 版本信息失败");
            throw new UpdateException(
                $"无法获取 umu-launcher 版本信息（请检查网络或代理）：{ex.Message}");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var assets = document.RootElement.GetProperty("assets")
                .EnumerateArray()
                .Select(a => (
                    Name: a.GetProperty("name").GetString() ?? "",
                    Url: a.GetProperty("browser_download_url").GetString() ?? ""))
                .ToArray();
            return SelectZipappAssetUrl(assets);
        }
        catch (JsonException ex)
        {
            throw new UpdateException($"umu-launcher 版本信息解析失败：{ex.Message}");
        }
        catch (KeyNotFoundException)
        {
            throw new UpdateException("umu-launcher 版本信息缺少 assets 字段（release 格式可能已变化）。");
        }
    }

    /// <summary>从 tar 包解出 umu-run（取第一个名为 umu-run 的条目）并补可执行位；返回完整路径。</summary>
    private static string ExtractUmuRun(string tarPath, string installDirectory)
    {
        var targetPath = Path.Combine(installDirectory, "umu-run");
        using (var stream = File.OpenRead(tarPath))
        using (var reader = ReaderFactory.Open(stream))
        {
            while (reader.MoveToNextEntry())
            {
                var entryName = Path.GetFileName(reader.Entry.Key ?? "");
                if (!reader.Entry.IsDirectory
                    && entryName.Equals("umu-run", StringComparison.Ordinal))
                {
                    // 只接受顶层单文件条目，拒绝路径穿越
                    reader.WriteEntryToFile(targetPath, new ExtractionOptions
                    {
                        ExtractFullPath = false,
                        Overwrite = true,
                    });
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(targetPath,
                            File.GetUnixFileMode(targetPath) | UnixFileMode.UserExecute);
                    }

                    return targetPath;
                }
            }
        }

        throw new UpdateException("zipapp 安装包里没有 umu-run 条目（release 格式可能已变化）。");
    }

    /// <summary>删除临时 tar（失败静默——临时文件不影响安装结果）。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
