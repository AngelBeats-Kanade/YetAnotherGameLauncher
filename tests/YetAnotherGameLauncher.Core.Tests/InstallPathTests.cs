using YetAnotherGameLauncher.Core;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests;

public class InstallPathTests
{
    [Fact]
    public void Resolve_AbsoluteInstallDir_IsUsedAsIs()
    {
        var absolute = OperatingSystem.IsWindows() ? "D:/Games/MyGame" : "/opt/games/mygame";

        var resolved = InstallPath.Resolve("somewhere", absolute);

        Assert.Equal(Path.GetFullPath(absolute), resolved);
    }

    [Fact]
    public void Resolve_RelativeInstallDir_IsRelativeToRoot()
    {
        var resolved = InstallPath.Resolve("~/Games", "WutheringWaves");

        Assert.Equal(Path.Combine(
            InstallPath.ExpandUserPath("~/Games"), "WutheringWaves"), resolved);
    }

    [Fact]
    public void ExpandUserPath_TildeExpandsToUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(home, InstallPath.ExpandUserPath("~"));
        Assert.Equal(Path.Combine(home, "Games"), InstallPath.ExpandUserPath("~/Games"));
        Assert.Equal("C:/plain", InstallPath.ExpandUserPath("C:/plain"));
    }
}
