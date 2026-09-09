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

    private GameLauncherService Service() => new(_runner);

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

        var plan = Service().BuildPlan(Game("wine \"{exe}\""), _tempDir.Path, "bin/game.exe");

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
    public async Task BuildPlan_MissingExecutable_Throws()
    {
        await Assert.ThrowsAsync<UpdateException>(
            () => Task.Run(() => Service().BuildPlan(Game("\"{exe}\""), _tempDir.Path, "bin/missing.exe")));
    }

    [Fact]
    public async Task LaunchAsync_ReturnsProcessExitCode()
    {
        await CreateExecutable();

        var exitCode = await Service().LaunchAsync(Game("\"{exe}\""), _tempDir.Path, "bin/game.exe");

        Assert.Equal(7, exitCode);
        var spec = Assert.Single(_runner.Specs);
        Assert.Equal(_tempDir.Path, spec.WorkingDirectory);
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
}
