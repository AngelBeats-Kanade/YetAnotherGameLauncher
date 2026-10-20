using System.Diagnostics;

namespace WineParentBridge.Verify;

/// <summary>
/// yagl-parent-bridge 的 Wine 自检器（2026-10-20 取代 verify.sh/verify-fixture.c）：
/// 1. 找到/发布 helper 与 Wine；
/// 2. 以挂起诱饵（helper 自身的 Games.exe 副本）为父进程运行 helper 自检模式 --write-parent；
/// 3. 断言游戏侧看到的父进程 = 诱饵路径，且 helper 退出后诱饵进程已被清理。
/// 用法：dotnet run --project tools/wine-parent-bridge/verify [-- --helper &lt;path&gt; --wine &lt;path&gt; --prefix &lt;path&gt;]。
/// </summary>
internal static class Program
{
    private const int FailureExitCode = 1;
    private const int TimeoutMilliseconds = 120_000;

    /// <summary>入口。</summary>
    public static int Main(string[] args)
    {
        var options = CommandLineOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return Run(options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            Console.Error.WriteLine($"FAIL: {ex.Message}");
            return FailureExitCode;
        }
    }

    /// <summary>自检主流程：准备 → Wine 运行 → 断言父进程 → 断言诱饵清理。</summary>
    private static int Run(CommandLineOptions options)
    {
        var repositoryRoot = FindRepositoryRoot();
        var workDirectory = options.WorkDirectory ?? Path.Combine(
            Path.GetTempPath(), "yagl-parent-bridge-verify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        try
        {
            var helper = ResolveHelper(options.HelperPath, repositoryRoot, workDirectory);
            var wine = ResolveWine(options.WinePath);
            var prefix = options.Prefix ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "yagl-parent-bridge-verify", "prefix");
            Directory.CreateDirectory(prefix);

            Console.WriteLine($"== helper: {helper}");
            Console.WriteLine($"== wine:   {wine}");
            Console.WriteLine($"== prefix: {prefix}");

            var decoy = Path.Combine(workDirectory, "Games.exe");
            var resultFile = Path.Combine(workDirectory, "parent-check.txt");
            var statusFile = Path.Combine(workDirectory, "bridge-status.txt");
            File.Copy(helper, decoy, overwrite: true);
            if (File.Exists(resultFile))
            {
                File.Delete(resultFile);
            }

            // 清掉上次失败运行可能残留的诱饵，保证"退出后无残留"断言只反映本次运行
            TryKillDecoy(wine, prefix);

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WINEPREFIX"] = prefix,
                ["YAGL_BRIDGE_DECOY"] = ToWindowsPath(decoy),
                ["YAGL_BRIDGE_TARGET"] = ToWindowsPath(helper),
                ["YAGL_BRIDGE_CMD"] =
                    $"\"{ToWindowsPath(helper)}\" --write-parent \"{ToWindowsPath(resultFile)}\"",
                ["YAGL_BRIDGE_CWD"] = ToWindowsPath(workDirectory),
                ["YAGL_BRIDGE_STATUS"] = ToWindowsPath(statusFile),
            };

            var bridgeExit = RunProcess(wine, [helper], environment, out _, out var standardError);
            Console.WriteLine($"bridge exit={bridgeExit}");
            if (bridgeExit != 0)
            {
                WriteCaptured("helper stderr", standardError);
                throw new InvalidOperationException($"helper 退出码 {bridgeExit}（期望 0）");
            }

            if (!File.Exists(statusFile))
            {
                throw new InvalidOperationException("helper 未写启动状态文件（READY 缺失）");
            }

            var bridgeStatus = File.ReadAllText(statusFile).Trim();
            if (!bridgeStatus.StartsWith("READY", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"启动状态不是 READY：{bridgeStatus}");
            }

            Console.WriteLine("PASS: helper 写出 READY 状态");

            // 失败通道：缺 YAGL_BRIDGE_DECOY 时 helper 必须退出 2 并写 ERROR:2:（游戏未启动）
            var failureStatusFile = Path.Combine(workDirectory, "bridge-status-error.txt");
            var failureEnvironment = new Dictionary<string, string>(environment, StringComparer.Ordinal)
            {
                ["YAGL_BRIDGE_STATUS"] = ToWindowsPath(failureStatusFile),
            };
            failureEnvironment.Remove("YAGL_BRIDGE_DECOY");
            var failureExit = RunProcess(
                wine, [helper], failureEnvironment, out _, out var failureError);
            if (failureExit != 2)
            {
                WriteCaptured("failure helper stderr", failureError);
                throw new InvalidOperationException($"缺输入时 helper 退出码 {failureExit}（期望 2）");
            }

            if (!File.Exists(failureStatusFile))
            {
                throw new InvalidOperationException("helper 失败时未写启动状态文件（ERROR 缺失）");
            }

            var failureStatus = File.ReadAllText(failureStatusFile).Trim();
            if (!failureStatus.StartsWith("ERROR:2:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"失败状态格式不符：{failureStatus}");
            }

            Console.WriteLine("PASS: helper 失败写出 ERROR 状态");

            if (!File.Exists(resultFile))
            {
                throw new InvalidOperationException("fixture 结果文件缺失（--write-parent 未执行）");
            }

            var actual = File.ReadAllText(resultFile).Trim();
            Console.WriteLine($"fixture says: {actual}");
            var expected = ToWindowsPath(decoy);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"父进程不是诱饵：期望 {expected}，实际 {actual}");
            }

            Console.WriteLine("PASS: 子进程父进程 = 诱饵 Games.exe");

            if (HasLeftoverDecoy(wine, prefix))
            {
                TryKillDecoy(wine, prefix);
                throw new InvalidOperationException("helper 退出后诱饵进程仍存活（清理失败）");
            }

            Console.WriteLine("PASS: 诱饵进程已被清理");
            return 0;
        }
        finally
        {
            if (!options.KeepWork)
            {
                TryDelete(workDirectory);
            }
            else
            {
                Console.WriteLine($"== work kept: {workDirectory}");
            }
        }
    }

    /// <summary>解析 helper：显式路径 → Core 构建产物（Debug/Release，且不旧于源码）→ 现场 dotnet publish。</summary>
    private static string ResolveHelper(string? explicitPath, string repositoryRoot, string workDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!File.Exists(explicitPath))
            {
                throw new FileNotFoundException($"helper 不存在：{explicitPath}");
            }

            return Path.GetFullPath(explicitPath);
        }

        var coreObjectDirectory = Path.Combine(
            repositoryRoot, "src", "YetAnotherGameLauncher.Core", "obj");
        var built = new[] { "Debug", "Release" }
            .Select(configuration => Path.Combine(
                coreObjectDirectory, configuration, "net10.0", "wine-parent-bridge",
                "yagl-parent-bridge.exe"))
            .FirstOrDefault(path => File.Exists(path) && IsNewerThanSources(path, repositoryRoot));
        if (built is not null)
        {
            return built;
        }

        var project = Path.Combine(
            repositoryRoot, "tools", "wine-parent-bridge", "WineParentBridge.csproj");
        var publishDirectory = Path.Combine(workDirectory, "publish");
        Console.WriteLine("== 未找到新鲜 Core 构建产物，dotnet publish helper…");
        var exitCode = RunProcess(
            "dotnet",
            ["publish", project, "-c", "Debug", "-r", "win-x64", "--self-contained", "true",
                "-o", publishDirectory],
            new Dictionary<string, string>(StringComparer.Ordinal),
            out var standardOutput,
            out var standardError);
        if (exitCode != 0)
        {
            WriteCaptured("dotnet publish stdout", standardOutput);
            WriteCaptured("dotnet publish stderr", standardError);
            throw new InvalidOperationException($"dotnet publish 失败（退出码 {exitCode}）");
        }

        var published = Path.Combine(publishDirectory, "yagl-parent-bridge.exe");
        if (!File.Exists(published))
        {
            throw new InvalidOperationException($"发布产物缺失：{published}");
        }

        return published;
    }

    /// <summary>
    /// 构建产物是否不旧于 helper 源码（2026-10-20 复审：dotnet run 不会重建 Core，
    /// 改完 helper 后旧二进制会让自检静默 PASS）。只比较顶层 .cs 与工程文件，verify/ 独立工程不计入。
    /// </summary>
    private static bool IsNewerThanSources(string helperPath, string repositoryRoot)
    {
        var sourceDirectory = Path.Combine(repositoryRoot, "tools", "wine-parent-bridge");
        var newestSource = Directory
            .EnumerateFiles(sourceDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Append(Path.Combine(sourceDirectory, "WineParentBridge.csproj"))
            .Where(File.Exists)
            .Max(File.GetLastWriteTimeUtc);
        return File.GetLastWriteTimeUtc(helperPath) >= newestSource;
    }

    /// <summary>解析 Wine：--wine → 环境变量 WINE → Steam compatibilitytools（取排序最后一个）→ PATH 里的 wine。</summary>
    private static string ResolveWine(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("WINE");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var compatibilityRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "Steam", "compatibilitytools.d");
        if (Directory.Exists(compatibilityRoot))
        {
            var candidates = Directory
                .EnumerateFiles(compatibilityRoot, "wine", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(
                    Path.Combine("files", "bin", "wine"), StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length > 0)
            {
                return candidates[^1];
            }
        }

        return "wine";
    }

    /// <summary>运行子进程并捕获输出；超时杀进程树。</summary>
    private static int RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        out string standardOutput,
        out string standardError)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动进程：{fileName}");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            standardOutput = outputTask.GetAwaiter().GetResult();
            standardError = errorTask.GetAwaiter().GetResult();
            throw new TimeoutException($"{fileName} 超时（{TimeoutMilliseconds} ms）");
        }

        standardOutput = outputTask.GetAwaiter().GetResult();
        standardError = errorTask.GetAwaiter().GetResult();
        return process.ExitCode;
    }

    /// <summary>用 wine tasklist 检查诱饵是否残留（tasklist 不可用时视为无残留并警告）。</summary>
    private static bool HasLeftoverDecoy(string wine, string prefix)
    {
        var exitCode = RunProcess(
            wine,
            ["tasklist"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["WINEPREFIX"] = prefix },
            out var output,
            out _);
        if (exitCode != 0)
        {
            Console.Error.WriteLine("WARN: wine tasklist 失败，跳过诱饵残留检查");
            return false;
        }

        return output
            .Split('\n')
            .Any(line => line.Contains("Games.exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>清理残留诱饵（best-effort，仅当前 prefix）。</summary>
    private static void TryKillDecoy(string wine, string prefix)
    {
        try
        {
            RunProcess(
                wine,
                ["taskkill", "/IM", "Games.exe", "/F"],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["WINEPREFIX"] = prefix },
                out _,
                out _);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception or TimeoutException)
        {
            Console.Error.WriteLine($"WARN: 清理残留诱饵失败：{ex.Message}");
        }
    }

    /// <summary>把 Linux 路径映射成 Wine 的 Z: 盘路径。</summary>
    private static string ToWindowsPath(string path) => "Z:" + path.Replace('/', '\\');

    /// <summary>从运行目录向上找到仓库根（以 tools/wine-parent-bridge/WineParentBridge.csproj 为标记）。</summary>
    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var marker = Path.Combine(
                directory.FullName, "tools", "wine-parent-bridge", "WineParentBridge.csproj");
            if (File.Exists(marker))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("找不到仓库根（tools/wine-parent-bridge/WineParentBridge.csproj）");
    }

    /// <summary>打印失败输出（截断到 4000 字符，避免刷屏）。</summary>
    private static void WriteCaptured(string label, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Console.Error.WriteLine($"-- {label}:");
        Console.Error.WriteLine(text.Length > 4000 ? text[..4000] : text);
    }

    /// <summary>递归删除目录（best-effort）。</summary>
    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"WARN: 临时目录清理失败：{ex.Message}");
        }
    }

    /// <summary>打印用法。</summary>
    private static void PrintUsage()
    {
        Console.WriteLine("""
            yagl-parent-bridge 自检器（Wine 侧）

            用法:
              dotnet run --project tools/wine-parent-bridge/verify [选项] [helper路径]

            选项:
              --helper <path>   helper exe 路径（缺省用 Core 构建产物，必要时 dotnet publish）
              --wine <path>     wine 可执行文件（缺省 $WINE 或 Steam compatibilitytools）
              --prefix <path>   WINEPREFIX（缺省 ~/.cache/yagl-parent-bridge-verify/prefix）
              --work <path>     工作目录（缺省临时目录）
              --keep-work       保留工作目录
              --help            显示本帮助
            """);
    }

    /// <summary>命令行选项。</summary>
    private sealed record CommandLineOptions(
        string? HelperPath,
        string? WinePath,
        string? Prefix,
        string? WorkDirectory,
        bool KeepWork,
        bool ShowHelp)
    {
        /// <summary>解析 argv；未知选项与缺参数直接抛错（由 Main 统一打印）。</summary>
        public static CommandLineOptions Parse(string[] args)
        {
            string? helper = null;
            string? wine = null;
            string? prefix = null;
            string? work = null;
            var keepWork = false;
            var showHelp = false;

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                switch (argument)
                {
                    case "--helper":
                        helper = RequireValue(args, ref index, argument);
                        break;
                    case "--wine":
                        wine = RequireValue(args, ref index, argument);
                        break;
                    case "--prefix":
                        prefix = RequireValue(args, ref index, argument);
                        break;
                    case "--work":
                        work = RequireValue(args, ref index, argument);
                        break;
                    case "--keep-work":
                        keepWork = true;
                        break;
                    case "--help" or "-h":
                        showHelp = true;
                        break;
                    default:
                        if (helper is null)
                        {
                            helper = argument;
                        }
                        else
                        {
                            throw new InvalidOperationException($"未知参数：{argument}");
                        }

                        break;
                }
            }

            return new CommandLineOptions(helper, wine, prefix, work, keepWork, showHelp);
        }

        /// <summary>取选项值；缺失即抛错。</summary>
        private static string RequireValue(string[] args, ref int index, string option) =>
            index + 1 < args.Length
                ? args[++index]
                : throw new InvalidOperationException($"{option} 缺少参数值");
    }
}
