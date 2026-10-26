using Xunit;
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
    [InlineData("../outside.txt")] // 逃逸：交 ResolveSafe 以 "escapes sandbox" 拒收
    public void IsReservedPath_Otherwise_ReturnsFalse(string path) =>
        Assert.False(ManifestBoundary.IsReservedPath(path), path);

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
