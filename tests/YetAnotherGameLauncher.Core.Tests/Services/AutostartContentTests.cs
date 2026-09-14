using Xunit;
using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class AutostartContentTests
{
    [Fact]
    public void BuildDesktopContent_QuotesExecPath()
    {
        var content = LinuxAutostartService.BuildDesktopContent("/opt/yagl/YetAnotherGameLauncher");

        Assert.Contains("[Desktop Entry]", content);
        Assert.Contains("Exec=\"/opt/yagl/YetAnotherGameLauncher\"", content);
        Assert.Contains("Type=Application", content);
    }

    [Fact]
    public void DesktopFilePath_UnderXdgAutostart()
    {
        var path = LinuxAutostartService.DesktopFilePath("/home/user");

        Assert.StartsWith("/home/user", path);
        Assert.EndsWith(Path.Combine(".config", "autostart", "yetanothergamelauncher.desktop"), path);
    }
}
