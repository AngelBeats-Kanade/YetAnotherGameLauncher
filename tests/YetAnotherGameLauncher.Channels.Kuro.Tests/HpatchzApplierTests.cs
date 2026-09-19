using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

public class HpatchzApplierTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeProcessRunner _runner = new();

    public void Dispose() => _tempDir.Dispose();

    /// <summary>造一个"存在且可执行"的 hpatchz 替身：预检会查盘，Linux 还要求执行位。</summary>
    private string StubTool()
    {
        var path = _tempDir.FilePath("hpatchz-stub");
        File.WriteAllText(path, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    [Fact]
    public async Task ApplyAsync_BareName_Windows_TriesExeFallbackOnPath()
    {
        // Windows CI 腿（2026-09-19）：裸命令名的 PATH 预检必须补试 ".exe"——CreateProcess
        // 会自动补全而 File.Exists 不会，不补这一刀会在 Windows 上误报"补丁工具缺失"
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip(".exe 补试是 Windows CreateProcess 语义，POSIX 无对应行为");
        }

        var toolsDir = _tempDir.FilePath("tools");
        Directory.CreateDirectory(toolsDir);
        File.WriteAllBytes(Path.Combine(toolsDir, "hpatchz.exe"), [0x01]); // Windows：存在即可执行

        // 前置追加（不替换）进程 PATH 并在 finally 还原：其余并行测试的 PATH 视图只多出本目录
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", toolsDir + Path.PathSeparator + previousPath);
        try
        {
            var applier = new HpatchzApplier(_runner, new HpatchzApplierOptions { HpatchzPath = "hpatchz" });
            var patch = _tempDir.FilePath("patch.krpdiff");
            await File.WriteAllTextAsync(patch, "stub");

            await applier.ApplyAsync(patch, _tempDir.FilePath("old"), _tempDir.FilePath("new"));

            var spec = Assert.Single(_runner.Specs);
            Assert.Equal(Path.Combine(toolsDir, "hpatchz.exe"), spec.FileName); // 补试 .exe 命中
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

    [Fact]
    public async Task ApplyAsync_BuildsDirectoryModeCommandWithQuotedPaths()
    {
        var tool = StubTool();
        var applier = new HpatchzApplier(_runner, new HpatchzApplierOptions { HpatchzPath = tool });
        var patch = _tempDir.FilePath("patch.krpdiff");
        await File.WriteAllTextAsync(patch, "stub");

        await applier.ApplyAsync(patch, _tempDir.FilePath("old"), _tempDir.FilePath("new"));

        var spec = Assert.Single(_runner.Specs);
        Assert.Equal(tool, spec.FileName);
        Assert.Contains("-f", spec.Arguments);
        Assert.Contains($"\"{_tempDir.FilePath("old")}\"", spec.Arguments);
        Assert.Contains($"\"{patch}\"", spec.Arguments);
        Assert.Contains($"\"{_tempDir.FilePath("new")}\"", spec.Arguments);
    }

    [Fact]
    public async Task ApplyAsync_CreatesOutputDirectory()
    {
        var applier = new HpatchzApplier(_runner, new HpatchzApplierOptions { HpatchzPath = StubTool() });
        var patch = _tempDir.FilePath("patch.krpdiff");
        await File.WriteAllTextAsync(patch, "stub");

        await applier.ApplyAsync(patch, _tempDir.FilePath("old"), _tempDir.FilePath("deep", "new"));

        Assert.True(Directory.Exists(_tempDir.FilePath("deep", "new")));
    }

    [Fact]
    public async Task ApplyAsync_NonZeroExit_ThrowsWithStderr()
    {
        _runner.Handler = _ => new ProcessResult(2, "", "diff data corrupted");
        var applier = new HpatchzApplier(_runner, new HpatchzApplierOptions { HpatchzPath = StubTool() });
        var patch = _tempDir.FilePath("patch.krpdiff");
        await File.WriteAllTextAsync(patch, "stub");

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => applier.ApplyAsync(patch, _tempDir.FilePath("old"), _tempDir.FilePath("new")));

        Assert.Contains("2", ex.Message);
        Assert.Contains("diff data corrupted", ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_PassesConfiguredTimeout()
    {
        var applier = new HpatchzApplier(_runner, new HpatchzApplierOptions
        {
            HpatchzPath = StubTool(),
            TimeoutMilliseconds = 1234,
        });
        var patch = _tempDir.FilePath("patch.krpdiff");
        await File.WriteAllTextAsync(patch, "stub");

        await applier.ApplyAsync(patch, _tempDir.FilePath("old"), _tempDir.FilePath("new"));

        Assert.Equal(1234, Assert.Single(_runner.Specs).TimeoutMilliseconds);
    }

    [Fact]
    public async Task ApplyAsync_ToolMissing_ThrowsWithActionableHint_BeforeSpawning()
    {
        // 预检先行：缺失时给出可操作错误，而不是等 CreateProcess 抛难懂的 Win32Exception
        var applier = new HpatchzApplier(
            _runner, new HpatchzApplierOptions { HpatchzPath = _tempDir.FilePath("no-such-tool") });
        var patch = _tempDir.FilePath("patch.krpdiff");
        await File.WriteAllTextAsync(patch, "stub");

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => applier.ApplyAsync(patch, _tempDir.FilePath("old"), _tempDir.FilePath("new")));

        Assert.Contains(_tempDir.FilePath("no-such-tool"), ex.Message);
        Assert.Empty(_runner.Specs); // 未触达进程执行
    }

    [Fact]
    public async Task ApplyAsync_ToolWithoutExecBit_Linux_ThrowsBeforeSpawning()
    {
        if (OperatingSystem.IsWindows())
        {
            // 审计修复（2026-09-19）：静默 return 改为可见 Skip（计入 Skipped 摘要）。
            // Windows 无执行位概念，用例仅在 Linux 上有意义。
            Assert.Skip("执行位校验是 POSIX 专属行为，Windows 无对应语义");
        }

        var path = _tempDir.FilePath("hpatchz-noexec");
        File.WriteAllText(path, "stub"); // 故意不加执行位
        var applier = new HpatchzApplier(_runner, new HpatchzApplierOptions { HpatchzPath = path });
        var patch = _tempDir.FilePath("patch.krpdiff");
        await File.WriteAllTextAsync(patch, "stub");

        await Assert.ThrowsAsync<UpdateException>(
            () => applier.ApplyAsync(patch, _tempDir.FilePath("old"), _tempDir.FilePath("new")));

        Assert.Empty(_runner.Specs);
    }
}
