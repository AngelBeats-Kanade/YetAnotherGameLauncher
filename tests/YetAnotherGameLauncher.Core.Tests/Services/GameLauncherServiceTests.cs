using System.ComponentModel;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class GameLauncherServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeProcessRunner _runner = new()
    {
        Handler = _ => new ProcessResult(7, "", ""),
    };

    public void Dispose() => _tempDir.Dispose();

    private GameLauncherService Service(string? pathValue = null, string? logDir = null) =>
        new(_runner, logDirectory: logDir ?? _tempDir.FilePath("logs"), pathValue: pathValue);

    private GameDefinition Game(string commandTemplate = "{exe}") => new()
    {
        Id = "test",
        DisplayName = "测试",
        Channel = "kuro",
        InstallDir = "Test",
        Executable = "bin/game.exe",
        Launch = new LaunchOptions
        {
            CommandTemplate = commandTemplate,
            WorkingDirectory = "{installDir}",
            Environment = new Dictionary<string, string> { ["GAME_DIR"] = "{installDir}" },
        },
    };

    private async Task<string> CreateExecutable()
    {
        Directory.CreateDirectory(_tempDir.FilePath("bin"));
        await File.WriteAllTextAsync(_tempDir.FilePath("bin", "game.exe"), "stub");
        return _tempDir.FilePath("bin", "game.exe");
    }

    [Fact]
    public async Task BuildPlan_ExpandsExePlaceholder()
    {
        var exePath = await CreateExecutable();

        var plan = Service().BuildPlan(Game("\"{exe}\""), _tempDir.Path, "bin/game.exe");

        Assert.Equal(exePath, plan.FileName);
        Assert.Equal("", plan.Arguments);
        Assert.Equal(_tempDir.Path, plan.WorkingDirectory);
    }

    [Fact]
    public async Task BuildPlan_BareExeTemplate_QuotesSpacedPaths()
    {
        // 用户场景：裸 {exe} 模板 + 含空格的安装路径——不加引号会在按空格切分时被截断
        var spacedDir = _tempDir.FilePath("Wuthering Waves Games");
        Directory.CreateDirectory(spacedDir);
        var exePath = Path.Combine(spacedDir, "game.exe");
        await File.WriteAllTextAsync(exePath, "stub");

        var plan = Service().BuildPlan(Game("{exe}"), _tempDir.Path, "Wuthering Waves Games/game.exe");

        Assert.Equal(exePath, plan.FileName);
        Assert.Equal("", plan.Arguments);
        Assert.Equal(_tempDir.Path, plan.WorkingDirectory);
    }

    [Fact]
    public async Task BuildPlan_WineTemplate_SplitsCommandAndArgs()
    {
        await CreateExecutable();
        var winePath = await CreatePathStub("wine");

        // 预检要能在注入的 PATH 里找到 wine
        var plan = Service(pathValue: _tempDir.FilePath("pathbin"))
            .BuildPlan(Game("wine \"{exe}\""), _tempDir.Path, "bin/game.exe");

        Assert.Equal("wine", plan.FileName);
        Assert.StartsWith("\"", plan.Arguments);
        Assert.EndsWith("game.exe\"", plan.Arguments);
    }

    [Fact]
    public async Task BuildPlan_ExpandsEnvironmentValues()
    {
        await CreateExecutable();

        var plan = Service().BuildPlan(Game("\"{exe}\""), _tempDir.Path, "bin/game.exe");

        Assert.Equal(_tempDir.Path, plan.Environment["GAME_DIR"]);
    }

    [Fact]
    public async Task BuildPlan_WorkingDirectoryFallsBackToInstallDir()
    {
        await CreateExecutable();
        var game = Game("\"{exe}\"");
        game.Launch.WorkingDirectory = "";

        var plan = Service().BuildPlan(game, _tempDir.Path, "bin/game.exe");

        Assert.Equal(_tempDir.Path, plan.WorkingDirectory);
    }

    [Fact]
    public async Task BuildPlan_MissingExecutable_ThrowsCategorized()
    {
        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => Task.Run(() => Service().BuildPlan(Game("\"{exe}\""), _tempDir.Path, "bin/missing.exe")));

        Assert.Equal(LaunchFailureKind.ExecutableMissing, ex.Kind);
        Assert.Contains("不存在", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildPlan_BareRuntimeMissingOnPath_ThrowsCategorized()
    {
        await CreateExecutable();

        // pathValue 空串 = PATH 扫描禁用 = wine 找不到
        var ex = await Assert.ThrowsAsync<LaunchException>(() => Task.Run(() =>
            Service(pathValue: "").BuildPlan(Game("wine \"{exe}\""), _tempDir.Path, "bin/game.exe")));

        Assert.Equal(LaunchFailureKind.RuntimeMissing, ex.Kind);
        Assert.Contains("wine", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildPlan_AbsoluteRuntimeMissing_ThrowsCategorized()
    {
        await CreateExecutable();
        var missingScript = _tempDir.FilePath("compat", "proton");

        var ex = await Assert.ThrowsAsync<LaunchException>(() => Task.Run(() =>
            Service().BuildPlan(
                Game($"\"{missingScript}\" run {{exe}}"), _tempDir.Path, "bin/game.exe")));

        Assert.Equal(LaunchFailureKind.RuntimeMissing, ex.Kind);
    }

    [Fact]
    public async Task BuildPlan_AbsoluteRuntimeWithoutExecBit_IsFixedAutomatically()
    {
        // 从压缩包解出的 proton 脚本常缺执行位：预检自动补 +x 而不是直接报错
        if (OperatingSystem.IsWindows())
        {
            return; // Windows 无执行位概念
        }

        await CreateExecutable();
        var script = _tempDir.FilePath("compat", "proton");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        await File.WriteAllTextAsync(script, "#!/bin/sh");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Service().BuildPlan(Game($"\"{script}\" run {{exe}}"), _tempDir.Path, "bin/game.exe");

        Assert.True(
            File.GetUnixFileMode(script).HasFlag(UnixFileMode.UserExecute),
            "预检应自动补上可执行位");
    }

    [Fact]
    public async Task BuildPlan_CreatesWinePrefixDirectory()
    {
        // Proton 要求 STEAM_COMPAT_DATA_PATH 目录已存在；预检负责创建
        await CreateExecutable();
        var prefix = _tempDir.FilePath("prefix");
        var game = Game("\"{exe}\"");
        game.Launch.Environment["WINEPREFIX"] = prefix;

        Service().BuildPlan(game, _tempDir.Path, "bin/game.exe");

        Assert.True(Directory.Exists(prefix));
    }

    [Fact]
    public async Task LaunchAsync_ReturnsProcessExitCodeAndLogPath()
    {
        await CreateExecutable();
        var logDir = _tempDir.FilePath("logs");

        var result = await Service(logDir: logDir).LaunchAsync(Game("\"{exe}\""), _tempDir.Path, "bin/game.exe");

        Assert.Equal(7, result.ExitCode);
        Assert.StartsWith(logDir, result.LogPath, StringComparison.Ordinal);
        Assert.Contains("launch-test-", Path.GetFileName(result.LogPath!), StringComparison.Ordinal);
        var spec = Assert.Single(_runner.Specs);
        Assert.Equal(_tempDir.Path, spec.WorkingDirectory);
        Assert.Equal(result.LogPath, spec.OutputLogPath);
    }

    [Fact]
    public async Task LaunchAsync_IsFireAndForget()
    {
        // 游戏启动不得等待进程退出：等 10 分钟超时后整个游戏进程树会被启动器杀死
        await CreateExecutable();

        await Service().LaunchAsync(Game("\"{exe}\""), _tempDir.Path, "bin/game.exe");

        var spec = Assert.Single(_runner.Specs);
        Assert.False(spec.WaitForExit);
    }

    [Fact]
    public async Task LaunchAsync_StartFailure_WrappedAsCategorizedError()
    {
        await CreateExecutable();
        var runner = new FakeProcessRunner
        {
            Handler = _ => throw new Win32Exception(13, "Permission denied"),
        };

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => new GameLauncherService(runner, logDirectory: _tempDir.FilePath("logs"))
                .LaunchAsync(Game("\"{exe}\""), _tempDir.Path, "bin/game.exe"));

        Assert.Equal(LaunchFailureKind.StartFailed, ex.Kind);
    }

    [Fact]
    public async Task SanitizeGameId_UnsafeCharacters_ReplacedForLogFileName()
    {
        await CreateExecutable();
        var game = Game("\"{exe}\"");
        game.Id = "wuthering waves/global:cn";

        var result = await Service().LaunchAsync(game, _tempDir.Path, "bin/game.exe");

        var name = Path.GetFileName(result.LogPath!);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain(":", name);
        Assert.Matches(@"^launch-wuthering-waves-global-cn-\d{8}-\d{6}\.log$", name);
    }

    [Theory]
    [InlineData("\"C:/Program Files/game.exe\" -dx11", "C:/Program Files/game.exe", "-dx11")]
    [InlineData("wine game.exe", "wine", "game.exe")]
    [InlineData("game.exe", "game.exe", "")]
    public void SplitCommand_HandlesQuotesAndArguments(string command, string expectedFile, string expectedArgs)
    {
        var (fileName, arguments) = GameLauncherService.SplitCommand(command);

        Assert.Equal(expectedFile, fileName);
        Assert.Equal(expectedArgs, arguments);
    }

    /// <summary>在注入 PATH 目录下创建一个带可执行位的桩命令，返回完整路径。</summary>
    private async Task<string> CreatePathStub(string name)
    {
        var dir = _tempDir.FilePath("pathbin");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        await File.WriteAllTextAsync(path, "#!/bin/sh");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
