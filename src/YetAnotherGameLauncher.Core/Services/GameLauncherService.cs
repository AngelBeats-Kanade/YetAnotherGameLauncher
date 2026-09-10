using System.ComponentModel;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>一次游戏启动的结果：退出码（即启即走时恒为 0）与启动日志路径。</summary>
public sealed record LaunchResult(int ExitCode, string? LogPath);

/// <summary>
/// 游戏启动服务：解析 LaunchOptions 中的命令模板（{exe} / {installDir} 占位符）、
/// 工作目录与环境变量，做启动预检（主程序存在、运行时可执行、prefix 可创建）后启动进程。
/// 游戏在 Linux 上如何运行（原生 / wine / Proton / umu）完全由配置决定，本服务不做平台假设。
/// </summary>
public sealed class GameLauncherService(
    IProcessRunner processRunner,
    ILogger? logger = null,
    string? logDirectory = null,
    string? pathValue = null)
{
    private readonly string _logDirectory = logDirectory
        ?? Path.Combine(AppPaths.DataDirectory, "logs");

    /// <summary>PATH 环境变量值（null = 真实环境；测试注入空串禁用 PATH 扫描）。</summary>
    private readonly string? _pathValue = pathValue;

    /// <summary>解析启动命令。commandTemplate 中以第一个未转义空格为界拆分为文件名与参数（支持引号）。</summary>
    public LaunchPlan BuildPlan(GameDefinition game, string installDir, string effectiveExecutable)
    {
        var exePath = Path.GetFullPath(Path.Combine(installDir, effectiveExecutable.Replace('\\', '/')));
        if (!File.Exists(exePath))
        {
            throw new LaunchException(
                LaunchFailureKind.ExecutableMissing,
                $"游戏主程序不存在：{exePath}。" +
                "请到「游戏设置 → 启动参数」检查可执行文件路径，或重新指定游戏安装目录。");
        }

        var template = game.Launch.CommandTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new UpdateException($"launch.commandTemplate for game {game.DisplayName} must not be empty.");
        }

        var command = Expand(template, exePath, installDir);
        var (fileName, arguments) = SplitCommand(command);
        ValidateCommand(fileName);

        var workingDirectory = string.IsNullOrWhiteSpace(game.Launch.WorkingDirectory)
            ? installDir
            : Expand(game.Launch.WorkingDirectory, exePath, installDir);

        var environment = game.Launch.Environment.ToDictionary(
            kv => kv.Key,
            kv => Expand(kv.Value, exePath, installDir),
            StringComparer.Ordinal);

        EnsurePrefixDirectories(environment);

        logger?.LogInformation("Launching {Game}: {File} {Args}", game.DisplayName, fileName, arguments);
        return new LaunchPlan(fileName, arguments, workingDirectory, environment);
    }

    /// <summary>
    /// 解析并启动游戏进程（即启即走：启动器不随游戏进程阻塞，更不会超时杀掉游戏）。
    /// 游戏输出（stdout/stderr）写入启动日志文件，秒退/报错可据此排查。
    /// </summary>
    public async Task<LaunchResult> LaunchAsync(
        GameDefinition game, string installDir, string effectiveExecutable, CancellationToken cancellationToken = default)
    {
        var plan = BuildPlan(game, installDir, effectiveExecutable);
        var logPath = ComposeLogPath(game.Id);
        ProcessStartSpec spec = new(
            plan.FileName, plan.Arguments, plan.WorkingDirectory, plan.Environment,
            WaitForExit: false, OutputLogPath: logPath);
        try
        {
            var result = await processRunner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
            return new LaunchResult(result.ExitCode, logPath);
        }
        catch (Win32Exception ex)
        {
            logger?.LogWarning(ex, "Process start failed: {File}", plan.FileName);
            throw new LaunchException(
                LaunchFailureKind.StartFailed,
                $"无法启动进程「{plan.FileName}」：{ex.Message}（错误码 {ex.NativeErrorCode}）。",
                ex,
                logPath);
        }
    }

    /// <summary>启动预检：模板首段（运行时/解释器）必须存在且（Linux 上）可执行。</summary>
    private void ValidateCommand(string fileName)
    {
        var isAbsolute = Path.IsPathRooted(fileName);
        if (!isAbsolute)
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows 的 CreateProcess 自带 PATH 与 PATHEXT 解析，裸命令名交给它
                return;
            }

            if (CompatTools.FindOnPath(fileName, _pathValue) is null)
            {
                throw new LaunchException(
                    LaunchFailureKind.RuntimeMissing,
                    $"找不到启动命令「{fileName}」。" +
                    "可能的原因：Wine / umu-launcher 尚未安装（可在启动设置里一键安装 umu-launcher），" +
                    "或自定义启动命令里的程序名写错了。");
            }

            return;
        }

        if (!File.Exists(fileName))
        {
            throw new LaunchException(
                LaunchFailureKind.RuntimeMissing,
                $"启动命令「{fileName}」不存在。" +
                "可能原因：Proton / Wine 目录被移动或删除，可在启动设置里重新选择启动方式。");
        }

        if (!OperatingSystem.IsWindows() && !IsExecutable(fileName))
        {
            // 从压缩包解出来的 proton / umu 脚本常见缺执行位：能补就补，失败再报错
            try
            {
                File.SetUnixFileMode(fileName,
                    File.GetUnixFileMode(fileName) | UnixFileMode.UserExecute);
                logger?.LogInformation("Added user execute bit to {File}", fileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LaunchException(
                    LaunchFailureKind.RuntimeNotExecutable,
                    $"启动命令「{fileName}」没有可执行权限，且自动补授权失败。" +
                    $"请在终端执行：chmod +x \"{fileName}\"",
                    ex);
            }
        }
    }

    /// <summary>确保 WINEPREFIX / STEAM_COMPAT_DATA_PATH 指向的目录存在（Proton 要求目录已创建）。</summary>
    private void EnsurePrefixDirectories(IReadOnlyDictionary<string, string> environment)
    {
        foreach (var key in (string[])["WINEPREFIX", "STEAM_COMPAT_DATA_PATH"])
        {
            if (!environment.TryGetValue(key, out var dir) || string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                throw new LaunchException(
                    LaunchFailureKind.PrefixCreateFailed,
                    $"无法创建 Wine 前缀目录「{dir}」：{ex.Message}。请检查磁盘空间与目录权限。",
                    ex);
            }
        }
    }

    /// <summary>启动日志路径：{logDir}/launch-{gameId}-{yyyyMMdd-HHmmss}.log。</summary>
    private string ComposeLogPath(string gameId) => Path.Combine(
        _logDirectory,
        $"launch-{SanitizeGameId(gameId)}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    /// <summary>gameId 只保留文件名安全字符，其余替换为 '-'。</summary>
    private static string SanitizeGameId(string gameId)
    {
        var sanitized = new string(gameId.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return sanitized.Length == 0 ? "game" : sanitized;
    }

    /// <summary>Linux 可执行位检查（Windows 无此概念，存在即可）。</summary>
    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>替换模板占位符。{exe} 展开为带引号的路径——命令按空格切分，
    /// 路径含空格（如 D:\Wuthering Waves）不加引号会被截断成不存在的文件；兼容已手写引号的 "{exe}"。</summary>
    public static string Expand(string template, string exePath, string installDir)
    {
        var quoted = $"\"{exePath}\"";
        return template
            .Replace("\"{exe}\"", quoted, StringComparison.OrdinalIgnoreCase)
            .Replace("{exe}", quoted, StringComparison.OrdinalIgnoreCase)
            .Replace("{installDir}", installDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按引号感知规则把命令行拆分为文件名与参数（Windows 风格引号）。</summary>
    public static (string FileName, string Arguments) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
        {
            throw new ArgumentException("Command is empty.", nameof(command));
        }

        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            if (closingQuote > 0)
            {
                return (command[1..closingQuote], command[(closingQuote + 1)..].TrimStart());
            }
        }

        var firstSpace = command.IndexOf(' ');
        return firstSpace < 0
            ? (command, "")
            : (command[..firstSpace], command[(firstSpace + 1)..].TrimStart());
    }
}

/// <summary>一次游戏启动的完整计划。</summary>
public sealed record LaunchPlan(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);
