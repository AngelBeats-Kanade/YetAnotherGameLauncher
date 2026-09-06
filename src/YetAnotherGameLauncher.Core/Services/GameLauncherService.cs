using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// 游戏启动服务：解析 LaunchOptions 中的命令模板（{exe} / {installDir} 占位符）、
/// 工作目录与环境变量后启动进程。游戏在 Linux 上如何运行（原生 / wine / Proton / steam）
/// 完全由配置决定，本服务不做任何平台假设。
/// </summary>
public sealed class GameLauncherService(IProcessRunner processRunner, ILogger? logger = null)
{
    /// <summary>解析启动命令。commandTemplate 中以第一个未转义空格为界拆分为文件名与参数（支持引号）。</summary>
    public LaunchPlan BuildPlan(GameDefinition game, string installDir, string effectiveExecutable)
    {
        var exePath = Path.GetFullPath(Path.Combine(installDir, effectiveExecutable.Replace('\\', '/')));
        if (!File.Exists(exePath))
        {
            throw new UpdateException($"Game executable not found: {exePath}");
        }

        var template = game.Launch.CommandTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new UpdateException($"launch.commandTemplate for game {game.DisplayName} must not be empty.");
        }

        var command = Expand(template, exePath, installDir);
        var (fileName, arguments) = SplitCommand(command);

        var workingDirectory = string.IsNullOrWhiteSpace(game.Launch.WorkingDirectory)
            ? installDir
            : Expand(game.Launch.WorkingDirectory, exePath, installDir);

        var environment = game.Launch.Environment.ToDictionary(
            kv => kv.Key,
            kv => Expand(kv.Value, exePath, installDir),
            StringComparer.Ordinal);

        logger?.LogInformation("Launching {Game}: {File} {Args}", game.DisplayName, fileName, arguments);
        return new LaunchPlan(fileName, arguments, workingDirectory, environment);
    }

    /// <summary>解析并启动游戏进程。</summary>
    public async Task<int> LaunchAsync(
        GameDefinition game, string installDir, string effectiveExecutable, CancellationToken cancellationToken = default)
    {
        var plan = BuildPlan(game, installDir, effectiveExecutable);
        var result = await processRunner.RunAsync(
            new ProcessStartSpec(plan.FileName, plan.Arguments, plan.WorkingDirectory, plan.Environment),
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode;
    }

    /// <summary>替换模板占位符。</summary>
    public static string Expand(string template, string exePath, string installDir) =>
        template
            .Replace("{exe}", exePath, StringComparison.OrdinalIgnoreCase)
            .Replace("{installDir}", installDir, StringComparison.OrdinalIgnoreCase);

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
