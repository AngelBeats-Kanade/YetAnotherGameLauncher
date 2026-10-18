using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Core.Services.Umu;

/// <summary>诱饵父进程桥的准备结果：桥上文件已全部落盘（路径为主机路径）。</summary>
public sealed record DecoyParentPreparation(string BridgeDirectory, string DecoyPath, string HelperPath)
{
    /// <summary>诱饵可执行文件的 Windows 侧路径（Z: 盘映射；helper 以它为挂起父进程）。</summary>
    public string DecoyWindowsPath => DecoyParentBridge.ToWindowsPath(DecoyPath);
}

/// <summary>
/// 诱饵父进程桥（2026-10-18）：为终末地 1.6.3+ 的"启动者校验"准备一个挂起诱饵父进程。
///
/// 校验要求游戏进程的**直接父进程**满足三项：镜像文件名为 <c>Games.exe</c>、其 Authenticode
/// 签名可验证（WinVerifyTrust）、签名者证书指纹在白名单内（实测复现矩阵与机制见
/// artifacts/bugs.md 与 docs/ARCHITECTURE.md §3.1）。游戏目录里 PlatformProcess.exe /
/// UnityCrashHandler64.exe / Endfield.exe 与官方启动器 Games.exe 使用**同一张鹰角签名证书**，
/// 因此任选其一复制为 <c>Games.exe</c> 即可满足三项——这就是"诱饵父进程"（社区方案：
/// Hi3Helper.Plugin.Hypergryph 的 DECOY_PARENT_BRIDGE；零下载，不依赖官方启动器）。
///
/// 本类只做"准备"：挑选候选、复制诱饵、落盘 helper——实际挂起/父进程编排由 helper
/// （tools/wine-parent-bridge/ 的 yagl-parent-bridge.exe，嵌入式资源）在 wine 侧完成。
/// 任一环节失败都返回 null，由调用方回退直启（不阻断玩家）。
/// </summary>
public sealed class DecoyParentBridge(ILogger? logger = null)
{
    /// <summary>日志（准备失败记 Warning 时调用方回退直启；null = 静默）。</summary>
    private readonly ILogger? _logger = logger;

    /// <summary>诱饵候选文件（按序取首个存在者；全部是游戏目录里带鹰角签名的可执行文件）。</summary>
    public static readonly IReadOnlyList<string> CandidateFileNames =
        ["PlatformProcess.exe", "UnityCrashHandler64.exe", "Endfield.exe"];

    /// <summary>桥工作目录（相对游戏安装目录；与 .yagl 状态目录同级）。</summary>
    public const string StateDirectoryName = ".yagl";

    /// <summary>桥工作目录名（<c>.yagl/bridge</c>）。</summary>
    public const string BridgeDirectoryName = "bridge";

    /// <summary>诱饵文件名——校验要求父进程镜像文件名恰为 Games.exe（大小写不敏感）。</summary>
    public const string DecoyFileName = "Games.exe";

    /// <summary>桥 helper 文件名（嵌入式资源落盘名）。</summary>
    public const string HelperFileName = "yagl-parent-bridge.exe";

    /// <summary>helper 输入：诱饵可执行文件路径（Windows 路径）。</summary>
    public const string DecoyVariable = "YAGL_BRIDGE_DECOY";

    /// <summary>helper 输入：游戏可执行文件路径（Windows 路径，lpApplicationName）。</summary>
    public const string TargetVariable = "YAGL_BRIDGE_TARGET";

    /// <summary>helper 输入：游戏命令行（含引号 exe + 参数；缺省回退 TARGET）。</summary>
    public const string CommandLineVariable = "YAGL_BRIDGE_CMD";

    /// <summary>helper 输入：游戏工作目录（Windows 路径）。</summary>
    public const string WorkingDirectoryVariable = "YAGL_BRIDGE_CWD";

    /// <summary>
    /// 准备桥：挑选候选 → 复制为 <c>.yagl/bridge/Games.exe</c> → 落盘 helper。
    /// 返回 null 表示无候选或 IO 失败（调用方回退直启）。
    /// </summary>
    public DecoyParentPreparation? TryPrepare(string installDir)
    {
        try
        {
            var source = CandidateFileNames
                .Select(name => Path.Combine(installDir, name))
                .FirstOrDefault(File.Exists);
            if (source is null)
            {
                _logger?.LogWarning(
                    "诱饵父进程桥：{InstallDir} 下没有候选签名文件（{Candidates}），本次直启",
                    installDir, string.Join(" / ", CandidateFileNames));
                return null;
            }

            var bridgeDir = Path.Combine(installDir, StateDirectoryName, BridgeDirectoryName);
            Directory.CreateDirectory(bridgeDir);

            // 诱饵每次刷新：上次会话残留（宿主被强杀）与候选换版都被就地覆盖
            var decoyPath = Path.Combine(bridgeDir, DecoyFileName);
            File.Copy(source, decoyPath, overwrite: true);

            var helperPath = Path.Combine(bridgeDir, HelperFileName);
            var helperBytes = ReadEmbeddedHelper();
            if (!File.Exists(helperPath) ||
                !File.ReadAllBytes(helperPath).AsSpan().SequenceEqual(helperBytes))
            {
                // 先写临时文件再改名：避免并发/中断让桥上留下半个 helper
                var tempPath = helperPath + ".tmp";
                File.WriteAllBytes(tempPath, helperBytes);
                File.Move(tempPath, helperPath, overwrite: true);
            }

            _logger?.LogInformation("诱饵父进程桥就绪：{Decoy}（源 {Source}）", decoyPath, source);
            return new DecoyParentPreparation(bridgeDir, decoyPath, helperPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            _logger?.LogWarning(ex, "诱饵父进程桥准备失败：{InstallDir}（回退直启）", installDir);
            return null;
        }
    }

    /// <summary>把 Linux 绝对路径映射为 wine 的 Z: 盘 Windows 路径（<c>/a/b</c> → <c>Z:\a\b</c>）。</summary>
    public static string ToWindowsPath(string unixPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(unixPath);
        return "Z:" + unixPath.Replace('/', '\\');
    }

    /// <summary>读取嵌入式 helper（构建与自检见 tools/wine-parent-bridge/README.md）。</summary>
    internal static byte[] ReadEmbeddedHelper()
    {
        using var stream = typeof(DecoyParentBridge).Assembly
            .GetManifestResourceStream("yagl-parent-bridge.exe")
            ?? throw new InvalidOperationException(
                "嵌入式资源 yagl-parent-bridge.exe 缺失（Core.csproj 的 EmbeddedResource 配置错误）");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
