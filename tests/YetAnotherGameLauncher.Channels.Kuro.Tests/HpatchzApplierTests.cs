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
            return; // Windows 无执行位概念，用例仅在 Linux 上有意义
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
