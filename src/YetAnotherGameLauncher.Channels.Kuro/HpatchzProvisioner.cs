using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>
/// hpatchz 补丁工具的自动供给契约：PATH 无自备二进制时，从 HDiffPatch 官方 GitHub release
/// 下载固定版本工具包解压到应用数据目录（用户自备的 PATH 二进制始终优先，本供给器只作兜底）。
/// </summary>
public interface IHpatchzProvisioner
{
    /// <summary>确保自管 hpatchz 就绪（已就绪零网络），返回可执行文件完整路径；
    /// 无预编译资产的平台/架构或下载失败时抛 <see cref="UpdateException"/>（可操作文案）。</summary>
    Task<string> EnsureAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// hpatchz 自动供给器（2026-10-02）：对齐 FFmpeg 库/umu 组件的自动供给哲学——鸣潮增量更新
/// 不再要求用户手动安装 HDiffPatch。固定版本 v5.1.3 随 App 升级更换（不做在线版本检查）；
/// 资产 MD5 为 2026-10-02 官方 release 实测。下载复用 <see cref="IDownloader"/>（重试/限速/
/// MD5 校验内建），解压复用 <see cref="PackageInstallerService.ExtractArchive"/>（zip 防御
/// 解压），解压后显式补执行位（.NET ZipFile 不恢复 zip 内的 Unix 权限位）。
/// </summary>
public sealed class HpatchzProvisioner(
    IDownloader downloader,
    ILogger? logger = null,
    string? dataDirectory = null) : IHpatchzProvisioner
{
    /// <summary>供给的 HDiffPatch 版本（release tag 去 v 前缀）。</summary>
    public const string Version = "5.1.3";

    private const string DownloadRoot = "https://github.com/sisong/HDiffPatch/releases/download";

    /// <summary>Unix 下供给二进制的权限（0755：属主读写执行、组/其他读执行）。</summary>
    private const System.IO.UnixFileMode ExecutableMode =
        System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.UserExecute
        | System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.GroupExecute
        | System.IO.UnixFileMode.OtherRead | System.IO.UnixFileMode.OtherExecute;

    private string RootDirectory => Path.Combine(
        dataDirectory ?? AppPaths.DataDirectory, "tools", "hpatchz", $"v{Version}");

    /// <summary>目标平台/架构的资产选择：仅覆盖有实测 MD5 的组合（linux-x64 / windows-x64），
    /// 其余返回 null（调用方给「手动安装」的可操作提示）。internal 供单测。</summary>
    internal static (string AssetName, string Md5)? SelectAsset(bool isLinux, Architecture architecture) =>
        (isLinux, architecture) switch
        {
            (true, Architecture.X64) => ("linux64.zip", "1b66f06fea325eaa5e1f96c881c10459"),
            (false, Architecture.X64) => ("windows64.zip", "ec432fd2e20e9a6f8449aae9a9850d86"),
            _ => null,
        };

    /// <summary>资产下载直链。internal 供单测。</summary>
    internal static string BuildDownloadUrl(string assetName) =>
        $"{DownloadRoot}/v{Version}/hdiffpatch_v{Version}_bin_{assetName}";

    /// <inheritdoc />
    public async Task<string> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        var toolName = OperatingSystem.IsWindows() ? "hpatchz.exe" : "hpatchz";
        var toolPath = Path.Combine(RootDirectory, toolName);
        if (FileUtilities.IsExecutableFile(toolPath))
        {
            return toolPath; // 已就绪：零网络（PATH 自备优先由 HpatchzApplier 的解析顺序保证）
        }

        var asset = SelectAsset(OperatingSystem.IsLinux(), RuntimeInformation.ProcessArchitecture)
            ?? throw new UpdateException(
                $"No prebuilt hpatchz asset for this platform/architecture " +
                $"({(OperatingSystem.IsLinux() ? "linux" : "windows")}/{RuntimeInformation.ProcessArchitecture}). " +
                "Install HDiffPatch manually and make sure hpatchz is on PATH.");

        var root = RootDirectory;
        var tempExtract = root + ".extracting";
        var archivePath = root + ".zip";
        FileUtilities.TryDeleteDirectory(tempExtract);
        try
        {
            logger?.LogInformation("Downloading hpatchz {Version} ({Asset})…", Version, asset.AssetName);
            try
            {
                await downloader.DownloadFileAsync(
                    new DownloadRequest(BuildDownloadUrl(asset.AssetName), archivePath, null, asset.Md5),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DownloadException ex)
            {
                throw new UpdateException($"Could not download hpatchz v{Version}: {ex.Message}", ex);
            }

            Directory.CreateDirectory(root);
            PackageInstallerService.ExtractArchive(archivePath, tempExtract, "hpatchz");
            var extracted = Directory.EnumerateFiles(tempExtract, toolName, SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new UpdateException(
                    $"The downloaded hpatchz archive does not contain {toolName}; the release layout may have changed.");

            // .NET ZipFile 解压不恢复 zip 内的 Unix 权限位——显式补执行位（仅 Unix 有此概念）
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(extracted, ExecutableMode);
            }

            File.Move(extracted, toolPath, overwrite: true);
            logger?.LogInformation("hpatchz {Version} ready at {Path}", Version, toolPath);
            return toolPath;
        }
        finally
        {
            FileUtilities.TryDeleteDirectory(tempExtract, logger);
            FileUtilities.DeleteQuiet(archivePath);
        }
    }
}
