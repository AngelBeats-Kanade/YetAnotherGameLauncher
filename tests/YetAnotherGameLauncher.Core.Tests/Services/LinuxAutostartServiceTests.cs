using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>Linux 自启动实现：维护 ~/.config/autostart 的 XDG 桌面入口（home/exe 可注入隔离）。</summary>
public class LinuxAutostartServiceTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public async Task SetEnabledTrue_WritesDesktopEntry()
    {
        var service = new LinuxAutostartService(_home.Path, "/opt/yagl/launcher");

        await service.SetEnabledAsync(true);

        var path = LinuxAutostartService.DesktopFilePath(_home.Path);
        Assert.True(File.Exists(path));
        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("Exec=\"/opt/yagl/launcher\"", content, StringComparison.Ordinal);
        Assert.Contains("X-GNOME-Autostart-enabled=true", content, StringComparison.Ordinal);
        Assert.True(await service.IsEnabledAsync());
    }

    [Fact]
    public async Task SetEnabledFalse_RemovesDesktopEntry()
    {
        var service = new LinuxAutostartService(_home.Path, "/opt/yagl/launcher");
        await service.SetEnabledAsync(true);
        Assert.True(await service.IsEnabledAsync());

        await service.SetEnabledAsync(false);

        Assert.False(File.Exists(LinuxAutostartService.DesktopFilePath(_home.Path)));
        Assert.False(await service.IsEnabledAsync());
    }

    [Fact]
    public async Task IsEnabledAsync_NoEntry_ReturnsFalse()
    {
        var service = new LinuxAutostartService(_home.Path, "/opt/yagl/launcher");

        Assert.False(await service.IsEnabledAsync());
    }
}
