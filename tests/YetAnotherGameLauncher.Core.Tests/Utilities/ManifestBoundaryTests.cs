using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Tests.Utilities;

/// <summary>
/// 清单边界的落地语义归一（2026-10-26 复审）：禁写判定必须按 <c>ResolveSafe</c> 的实际落点算，
/// 词法比较会被 <c>./x</c>、<c>a/../x</c>、段尾点/尾空格这些同目标异写法绕过。
/// </summary>
public class ManifestBoundaryTests
{
    [Theory]
    [InlineData("Saved/slot.sav")]
    [InlineData("./Saved/slot.sav")]
    [InlineData("data/../Saved/slot.sav")]
    [InlineData("Saved./slot.sav")]
    [InlineData("Saved ./slot.sav")] // 尾点后再尾空格：Win32 连吃点与空格 → Saved
    [InlineData("Saved. /slot.sav")]
    [InlineData("Saved /slot.sav")]
    [InlineData(".yagl/state.json")]
    [InlineData("launcherDownloadConfig.json")]
    [InlineData("launcherDownloadConfig/mark")]
    [InlineData("compatdata/pfx/user.reg")]
    public void IsReservedPath_TargetLandsInReservedArea_ReturnsTrue(string path) =>
        Assert.True(ManifestBoundary.IsReservedPath(path), path);

    [Theory]
    [InlineData("Client/Content/HD/pak.pak")]
    [InlineData("data/file.dat")]
    [InlineData("SavedX/slot.sav")]
    [InlineData("Client/SavedX/slot.sav")]
    [InlineData("../outside.txt")] // 逃逸：交 ResolveSafe 以 "escapes sandbox" 拒收
    public void IsReservedPath_Otherwise_ReturnsFalse(string path) =>
        Assert.False(ManifestBoundary.IsReservedPath(path), path);

    [Theory]
    [InlineData("Client/Saved")]
    [InlineData("Client/Saved/SaveGames/slot.sav")]
    [InlineData("Client/Saved/Logs/Client.log")]
    [InlineData("Client/Saved/Resources/3.8.0/Option/pak.pak")]
    [InlineData("./Client/Saved/SaveGames/slot.sav")]
    public void IsReservedPath_ClientSaved_ReturnsTrue(string path) =>
        Assert.True(ManifestBoundary.IsReservedPath(path), path);

    [Theory]
    [InlineData("Client/Saved/Resources/3.8.0/Option/pak.pak", "3.8.0", false)]
    [InlineData("Client/Saved/Resources/3.8.0/Option/pak.pak", "3.7.1", true)]
    [InlineData("Client/Saved/Resources/3.8.0", "3.8.0", true)]
    [InlineData("Client/Saved/Resources/3.8.0/../SaveGames/slot.sav", "3.8.0", true)]
    [InlineData("Client/Saved/Resources/3.8.0X/Option/pak.pak", "3.8.0", true)]
    [InlineData("Client/Saved/Resources/3.8.0/Option/pak.pak", "3.8.0/../x", true)]
    [InlineData("Client/Saved/SaveGames/slot.sav", "3.8.0", true)]
    public void IsReservedPath_VersionedSavedResourcesAllowlist(string path, string allowedVersion, bool expected) =>
        Assert.Equal(expected, ManifestBoundary.IsReservedPath(path, allowedVersion));

    [Fact]
    public void ValidateManifest_ClientSavedResources_RequiresMatchingVersionAllowlist()
    {
        var manifest = ManifestWith("Client/Saved/Resources/3.8.0/Option/pak.pak");

        var ex = Assert.Throws<UpdateException>(
            () => ManifestBoundary.ValidateManifest(manifest, "test"));
        Assert.Contains("reserved", ex.Message);

        ManifestBoundary.ValidateManifest(manifest, "test", allowedSavedResourcesVersion: "3.8.0");

        Assert.Throws<UpdateException>(
            () => ManifestBoundary.ValidateManifest(manifest, "test", allowedSavedResourcesVersion: "3.7.1"));
    }

    [Fact]
    public void ValidateManifest_ClientSavedOtherPaths_RemainReservedEvenWithAllowlist()
    {
        var manifest = ManifestWith("Client/Saved/SaveGames/slot.sav");

        Assert.Throws<UpdateException>(
            () => ManifestBoundary.ValidateManifest(manifest, "test", allowedSavedResourcesVersion: "3.8.0"));
    }

    [Fact]
    public void ValidatePackFiles_ClientSavedResources_RequiresMatchingVersionAllowlist()
    {
        var files = new[]
        {
            File("Client/Content/UHD/pak.pak"),
            File("Client/Saved/Resources/3.8.0/Option_UHD/pak.pak"),
        };

        var ex = Assert.Throws<UpdateException>(
            () => ManifestBoundary.ValidatePackFiles("Client/Content/UHD", files, "test"));
        Assert.Contains("outside", ex.Message);

        ManifestBoundary.ValidatePackFiles(
            "Client/Content/UHD", files, "test", allowedSavedResourcesVersion: "3.8.0");

        Assert.Throws<UpdateException>(
            () => ManifestBoundary.ValidatePackFiles(
                "Client/Content/UHD", files, "test", allowedSavedResourcesVersion: "3.7.1"));
    }

    private static GameManifest ManifestWith(string path) => new()
    {
        Version = "3.8.0",
        Files = [File(path)],
    };

    private static ManifestFile File(string path) =>
        new(path, 1, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    [Fact]
    public void IsReservedPath_BlankOrNull_ReturnsFalse()
    {
        Assert.False(ManifestBoundary.IsReservedPath(null));
        Assert.False(ManifestBoundary.IsReservedPath(string.Empty));
        Assert.False(ManifestBoundary.IsReservedPath("   "));
    }

    [Theory]
    [InlineData("./Saved/slot.sav", "Saved/slot.sav")]
    [InlineData("data/../Saved/slot.sav", "Saved/slot.sav")]
    [InlineData("Saved ./slot.sav", "Saved/slot.sav")]
    [InlineData("Client\\Content\\HD\\pak.pak", "Client/Content/HD/pak.pak")]
    public void CanonicalizeWithinRoot_NormalizesToRealDestination(string path, string expected) =>
        Assert.Equal(expected, ManifestBoundary.CanonicalizeWithinRoot(path));

    [Fact]
    public void CanonicalizeWithinRoot_EscapingPath_ReturnsNull() =>
        Assert.Null(ManifestBoundary.CanonicalizeWithinRoot("../outside.txt"));
}
