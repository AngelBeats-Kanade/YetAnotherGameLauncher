using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>Windows 自启动实现：经 reg 子进程维护 HKCU Run 项（命令经假运行器断言）。</summary>
public class WindowsAutostartServiceTests
{
    private readonly FakeProcessRunner _runner = new()
    {
        Handler = _ => new ProcessResult(0, "", ""),
    };

    private WindowsAutostartService CreateService() => new(_runner);

    [Fact]
    public async Task IsEnabledAsync_QueriesRunKey_ReturnsExitCodeZero()
    {
        _runner.Handler = _ => new ProcessResult(0, "", "");

        Assert.True(await CreateService().IsEnabledAsync());
        var spec = Assert.Single(_runner.Specs);
        Assert.Equal("reg", spec.FileName);
        Assert.Contains("query HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v YetAnotherGameLauncher", spec.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsEnabledAsync_KeyMissing_ReturnsFalse()
    {
        _runner.Handler = _ => new ProcessResult(1, "", "");

        Assert.False(await CreateService().IsEnabledAsync());
    }

    [Fact]
    public async Task SetEnabledAsync_AddsRunEntryWithQuotedExe()
    {
        await CreateService().SetEnabledAsync(true);

        var spec = Assert.Single(_runner.Specs);
        Assert.Contains("add HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run", spec.Arguments, StringComparison.Ordinal);
        Assert.Contains("REG_SZ", spec.Arguments, StringComparison.Ordinal);
        Assert.Contains("\\\"", spec.Arguments, StringComparison.Ordinal); // exe 路径带引号
    }

    [Fact]
    public async Task SetEnabledAsync_Disabled_DeletesRunEntry()
    {
        await CreateService().SetEnabledAsync(false);

        var spec = Assert.Single(_runner.Specs);
        Assert.Contains("delete HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run", spec.Arguments, StringComparison.Ordinal);
    }
}
