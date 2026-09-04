using YetAnotherGameLauncher.Channels.Kuro;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

public class HpatchzApplierTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeProcessRunner _runner = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public async Task ApplyAsync_BuildsDirectoryModeCommandWithQuotedPaths()
    {
        var applier = new HpatchzApplier(_runner, new HpatchzApplierOptions { HpatchzPath = "/usr/bin/hpatchz" });
        var patch = _tempDir.FilePath("patch.krpdiff");
        await File.WriteAllTextAsync(patch, "stub");

        await applier.ApplyAsync(patch, _tempDir.FilePath("old"), _tempDir.FilePath("new"));

        var spec = Assert.Single(_runner.Specs);
        Assert.Equal("/usr/bin/hpatchz", spec.FileName);
        Assert.Contains("-f", spec.Arguments);
        Assert.Contains($"\"{_tempDir.FilePath("old")}\"", spec.Arguments);
        Assert.Contains($"\"{patch}\"", spec.Arguments);
        Assert.Contains($"\"{_tempDir.FilePath("new")}\"", spec.Arguments);
    }

    [Fact]
    public async Task ApplyAsync_CreatesOutputDirectory()
    {
        var applier = new HpatchzApplier(_runner);
        var patch = _tempDir.FilePath("patch.krpdiff");
        await File.WriteAllTextAsync(patch, "stub");

        await applier.ApplyAsync(patch, _tempDir.FilePath("old"), _tempDir.FilePath("deep", "new"));

        Assert.True(Directory.Exists(_tempDir.FilePath("deep", "new")));
    }

    [Fact]
    public async Task ApplyAsync_NonZeroExit_ThrowsWithStderr()
    {
        _runner.Handler = _ => new ProcessResult(2, "", "diff data corrupted");
        var applier = new HpatchzApplier(_runner);
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
        var applier = new HpatchzApplier(_runner, new HpatchzApplierOptions { TimeoutMilliseconds = 1234 });
        var patch = _tempDir.FilePath("patch.krpdiff");
        await File.WriteAllTextAsync(patch, "stub");

        await applier.ApplyAsync(patch, _tempDir.FilePath("old"), _tempDir.FilePath("new"));

        Assert.Equal(1234, Assert.Single(_runner.Specs).TimeoutMilliseconds);
    }
}
