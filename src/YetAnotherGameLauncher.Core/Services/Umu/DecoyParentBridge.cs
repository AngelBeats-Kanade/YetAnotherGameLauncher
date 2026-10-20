using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Core.Services.Umu;

/// <summary>诱饵父进程桥的准备结果：桥上文件已全部落盘（路径为主机路径）。</summary>
public sealed record DecoyParentPreparation(string BridgeDirectory, string DecoyPath, string HelperPath)
{
    /// <summary>诱饵可执行文件的 Windows 侧路径（Z: 盘映射；仅 Wine 链使用；Windows 原生链直接用 DecoyPath）。</summary>
    public string DecoyWinePath => DecoyParentBridge.ToWindowsPath(DecoyPath);
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
/// （tools/wine-parent-bridge/ 的 yagl-parent-bridge.exe，嵌入式资源）在 Windows 侧完成。
/// 桥工作目录自 2026-10-20 起由 <c>&lt;installDir&gt;/.yagl/bridge</c> 迁到应用数据目录
/// （<c>&lt;DataDirectory&gt;/bridge/&lt;gameId&gt;</c>），游戏安装目录只作为诱饵来源被读取；
/// 旧目录 best-effort 清理（但不触碰 .yagl 下的状态文件）。任一环节失败都返回 null，
/// 由调用方按平台回退（Linux 直启；Windows 报可操作错误）。
/// </summary>
public sealed class DecoyParentBridge(ILogger? logger = null)
{
    /// <summary>日志（准备失败记 Warning；调用方按平台处理：Linux 回退直启 / Windows 报错；null = 静默）。</summary>
    private readonly ILogger? _logger = logger;

    /// <summary>诱饵候选文件（按序取首个存在者；全部是游戏目录里带鹰角签名的可执行文件）。</summary>
    public static readonly IReadOnlyList<string> CandidateFileNames =
        ["PlatformProcess.exe", "UnityCrashHandler64.exe", "Endfield.exe"];

    /// <summary>旧版桥工作目录（相对游戏安装目录，2026-10-20 起仅用于迁移清理）。</summary>
    public const string LegacyStateDirectoryName = ".yagl";

    /// <summary>旧版桥子目录名（<c>.yagl/bridge</c>，仅用于迁移清理）。</summary>
    public const string LegacyBridgeDirectoryName = "bridge";

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

    /// <summary>helper 输出：启动状态文件路径（Windows 路径；YAGL launcher 启动路径必填并等待 READY）。</summary>
    public const string StatusVariable = "YAGL_BRIDGE_STATUS";

    /// <summary>helper 已成功创建游戏进程（此后透传的是游戏退出码，不是桥失败）。</summary>
    public const string ReadyStatus = "READY";

    /// <summary>桥失败状态前缀，格式 <c>ERROR:&lt;exitCode&gt;:&lt;message&gt;</c>。</summary>
    public const string ErrorStatusPrefix = "ERROR:";

    /// <summary>把 helper 失败退出码映射为可操作提示（契约见 tools/wine-parent-bridge/BridgeContract.cs）。</summary>
    public static string DescribeFailure(int? exitCode) => exitCode switch
    {
        2 => "诱饵父进程桥缺少启动参数（退出码 2），请重新启动启动器后重试。",
        3 => "诱饵父进程桥无法创建诱饵进程或设置父进程属性（退出码 3），可能被安全软件拦截。",
        4 => "游戏进程创建失败（退出码 4），可能被安全软件拦截或游戏文件不可用。",
        null => "诱饵父进程桥启动失败。",
        _ => $"诱饵父进程桥异常退出（退出码 {exitCode}）。",
    };

    /// <summary>
    /// 准备桥到指定工作目录：挑选候选 → 确保 <c>&lt;bridgeRoot&gt;/Games.exe</c> 与候选一致
    /// （内容相同则跳过覆盖）→ 落盘 helper；成功后 best-effort 清理旧版留在游戏安装目录的
    /// <c>.yagl/bridge</c>。返回 null 表示无候选或 IO 失败（调用方按平台决定回退/报错）。
    /// </summary>
    public DecoyParentPreparation? TryPrepare(string installDir, string bridgeRoot)
    {
        try
        {
            var source = CandidateFileNames
                .Select(name => Path.Combine(installDir, name))
                .FirstOrDefault(File.Exists);
            if (source is null)
            {
                _logger?.LogWarning(
                    "诱饵父进程桥：{InstallDir} 下没有候选签名文件（{Candidates}），" +
                    "本次启动按平台处理（Linux 回退直启 / Windows 报错）",
                    installDir, string.Join(" / ", CandidateFileNames));
                return null;
            }

            Directory.CreateDirectory(bridgeRoot);
            TryRemoveStaleStatusFiles(bridgeRoot);

            // 诱饵文件名必须是 Games.exe（校验按名字匹配），不能改名规避锁。
            // Windows 上已挂起的诱饵进程会锁住镜像文件：内容已与候选一致时跳过覆盖，
            // 使「第二次启动」与「helper 被强杀留下孤儿诱饵」仍能继续；内容不同才覆盖
            // （游戏换版后旧锁未释放时仍会失败，按准备失败处理）。
            var decoyPath = Path.Combine(bridgeRoot, DecoyFileName);
            if (!HasSameContent(decoyPath, source))
            {
                File.Copy(source, decoyPath, overwrite: true);
            }

            var helperPath = Path.Combine(bridgeRoot, HelperFileName);
            var helperBytes = ReadEmbeddedHelper();
            if (!File.Exists(helperPath) ||
                !File.ReadAllBytes(helperPath).AsSpan().SequenceEqual(helperBytes))
            {
                // 先写临时文件再改名：避免并发/中断让桥上留下半个 helper
                var tempPath = helperPath + ".tmp";
                File.WriteAllBytes(tempPath, helperBytes);
                File.Move(tempPath, helperPath, overwrite: true);
            }

            TryRemoveLegacyBridge(installDir, bridgeRoot);

            _logger?.LogInformation("诱饵父进程桥就绪：{Decoy}（源 {Source}）", decoyPath, source);
            return new DecoyParentPreparation(bridgeRoot, decoyPath, helperPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            _logger?.LogWarning(ex, "诱饵父进程桥准备失败：{InstallDir}（由调用方按平台处理）", installDir);
            return null;
        }
    }

    /// <summary>
    /// 清理崩溃/取消遗留的状态文件与半写临时文件：只删超过 1 天的（launcher 正常在 READY/ERROR/
    /// 超时后删除，活跃会话的 status 文件最多存在 30 秒，按时间阈值清理不会误删并发启动）。
    /// </summary>
    private static void TryRemoveStaleStatusFiles(string bridgeRoot)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(bridgeRoot, "status-*"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or NotSupportedException or ArgumentException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
        }
    }

    /// <summary>逐块比较文件内容（只读打开，可比较已被映射为挂起进程镜像的诱饵）；长度不同/不存在直接 false。</summary>
    private static bool HasSameContent(string path, string otherPath)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != new FileInfo(otherPath).Length)
        {
            return false;
        }

        const int bufferSize = 64 * 1024;
        using var left = File.OpenRead(path);
        using var right = File.OpenRead(otherPath);
        var leftBuffer = new byte[bufferSize];
        var rightBuffer = new byte[bufferSize];
        while (true)
        {
            var leftRead = left.Read(leftBuffer, 0, bufferSize);
            var rightRead = right.Read(rightBuffer, 0, bufferSize);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 清理旧版落在游戏安装目录的桥目录（best-effort）：只删 <c>.yagl/bridge</c> 子树，
    /// 绝不触碰 <c>.yagl</c> 下的状态/下载数据，也不删当前 bridgeRoot。
    /// </summary>
    private void TryRemoveLegacyBridge(string installDir, string bridgeRoot)
    {
        var legacyDir = Path.Combine(
            installDir, LegacyStateDirectoryName, LegacyBridgeDirectoryName);
        if (!Directory.Exists(legacyDir) || IsSamePath(legacyDir, bridgeRoot))
        {
            return;
        }

        try
        {
            Directory.Delete(legacyDir, recursive: true);
            _logger?.LogInformation("已清理旧版桥目录：{LegacyDir}", legacyDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            _logger?.LogWarning(ex, "旧版桥目录清理失败（不影响本次启动）：{LegacyDir}", legacyDir);
        }
    }

    /// <summary>路径等价比较（Windows 大小写不敏感；先规范化防止 <c>a/../b</c> 绕过自保护）。</summary>
    private static bool IsSamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
