using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>hpatchz 的执行规格：可能经 wine 间接运行（鸣潮 krpdiff 需要社区验证构建的
/// hpatchz.exe，Linux 上通过 wine 跑 PE——2026-10-02 实证：开源 HDiffPatch 全线构建
/// （官方 v4.8.0/v5.1.3、Windows/Linux、master 自编译）均无法读取鸣潮 3.7.0 的 krpdiff
/// 头部，退出码 109）。</summary>
/// <param name="FileName">进程可执行文件（原生二进制为其自身；wine 形态为 wine 路径）。</param>
/// <param name="ArgumentPrefix">exe 参数之前的前缀参数（原生为空；wine 形态为引号包裹的 exe 路径）。</param>
/// <param name="Environment">进程环境变量（wine 形态含 WINEPREFIX；原生为 null）。</param>
public sealed record HpatchzTool(
    string FileName,
    string ArgumentPrefix,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>
/// hpatchz 补丁工具的自动供给契约：PATH 无自备二进制时下载社区验证构建到应用数据目录
///（用户自备的 PATH 二进制始终优先，本供给器只作兜底）。
/// </summary>
public interface IHpatchzProvisioner
{
    /// <summary>确保自管 hpatchz 就绪（已就绪零网络），返回执行规格；
    /// 平台不受支持（无 wine 等）或下载失败时抛 <see cref="UpdateException"/>（可操作文案）。</summary>
    Task<HpatchzTool> EnsureAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// hpatchz 自动供给器（2026-10-02）：对齐 FFmpeg 库/umu 组件的自动供给哲学——鸣潮增量更新
/// 不再要求用户手动安装 HDiffPatch。
///
/// **构建选型（2026-10-02 真机实证链）**：鸣潮 3.7.0 的 krpdiff 头部（privateReserveDataSize 段）
/// 开源 HDiffPatch 全线构建均无法读取（官方 release v4.8.0 / v5.1.3 的 Linux 与 Windows 构建、
/// master 源码自编译，一律退出码 109 "load dir data in diffFile ERROR"）；唯一实测可用的是
/// ww-manager（wutheringwaves-cli-manager）release 打包的 hpatchz.exe（版本串 v4.8.0、与官方
/// v4.8.0 windows64 构建不同 md5——来源未注明，推断为官方启动器配套的定制构建；对 group_0 的
/// 实际产出与官方清单 size/MD5 一致，且应用链有 dstFiles 事后校验兜底）。资产固定为该 exe，
/// MD5 为 2026-10-02 实测；Linux 上经 wine（系统 wine 或已装 Proton 自带 wine）运行 PE，
/// 使用独立 WINEPREFIX 不污染用户环境。
/// </summary>
public sealed class HpatchzProvisioner(
    IDownloader downloader,
    ILogger? logger = null,
    string? dataDirectory = null,
    Func<string?>? wineLocator = null) : IHpatchzProvisioner
{
    /// <summary>供给的构建标识（ww-manager 打包的 hpatchz.exe，目录名与 URL 段）。</summary>
    public const string Version = "ww-v4.8.0";

    /// <summary>资产直链与实测 MD5（2026-10-02）。</summary>
    internal const string DownloadUrl =
        "https://github.com/timetetng/wutheringwaves-cli-manager/releases/download/hpatchz-v4.8.0/hpatchz.exe";
    internal const string AssetMd5 = "c14098fb69c393348a28503d3559bc2c";

    private string RootDirectory => Path.Combine(
        dataDirectory ?? AppPaths.DataDirectory, "tools", "hpatchz", Version);

    private string ExePath => Path.Combine(RootDirectory, "hpatchz.exe");

    private string PrefixPath => Path.Combine(RootDirectory, "prefix");

    /// <summary>unix 路径 → wine Z: 盘形态（wine 默认把根 / 映射为 Z:；正斜杠 Windows 侧同样接受，
    /// 2026-10-02 Proton wine 实测）。</summary>
    internal static string ToWinePath(string unixPath) => "Z:" + unixPath;

    /// <inheritdoc />
    public async Task<HpatchzTool> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        // 就绪判定按平台：Windows 存在即可执行；Linux 下 exe 由 wine 消费、不需要（也不会有）
        // Unix 执行位——若在此误用 IsExecutableFile 会恒判未就绪、每次重下（幂等破坏）
        var isReady = OperatingSystem.IsWindows()
            ? FileUtilities.IsExecutableFile(ExePath)
            : File.Exists(ExePath);
        if (!isReady)
        {
            Directory.CreateDirectory(RootDirectory);
            logger?.LogInformation("Downloading hpatchz {Version}…", Version);
            try
            {
                await downloader.DownloadFileAsync(
                    new DownloadRequest(DownloadUrl, ExePath, null, AssetMd5),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DownloadException ex)
            {
                throw new UpdateException($"Could not download hpatchz ({Version}): {ex.Message}", ex);
            }

            logger?.LogInformation("hpatchz {Version} ready at {Path}", Version, ExePath);
        }

        if (OperatingSystem.IsWindows())
        {
            return new HpatchzTool(ExePath, ArgumentPrefix: "");
        }

        var wine = wineLocator?.Invoke()
            ?? CompatTools.FindSystemWine()
            ?? CompatTools.FindProtonWine();
        if (wine is null || !FileUtilities.IsExecutableFile(wine))
        {
            throw new UpdateException(
                "hpatchz runs through wine, but no wine was found (neither a system wine nor one bundled " +
                "with an installed Proton). Install wine, or provide a native hpatchz binary on PATH.");
        }

        // 独立 prefix：不污染用户 ~/.wine；首次运行 wine 会自动完成 prefix 初始化（纯 CLI 程序无 GUI 依赖）
        return new HpatchzTool(
            wine,
            ArgumentPrefix: Quote(ExePath),
            Environment: new Dictionary<string, string> { ["WINEPREFIX"] = PrefixPath });
    }

    /// <summary>按 Windows 命令行分词语义包裹单个参数（wine 侧按 CreateProcess 分词解析）。</summary>
    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
