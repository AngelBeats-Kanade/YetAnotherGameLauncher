using YetAnotherGameLauncher.Services;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>原生 umu 组件准备器的纯逻辑（不碰网络）。</summary>
public sealed class UmuComponentProvisionerTests
{
    [Fact]
    public void ParseSha256For_FindsMatchingArchiveLine()
    {
        const string sums = """
            abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  SteamLinuxRuntime_sniper.tar.xz
            1111111111111111111111111111111111111111111111111111111111111111  other.tar.xz
            """;
        var sha = UmuComponentProvisioner.ParseSha256For(sums, "SteamLinuxRuntime_sniper.tar.xz");
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", sha);
    }

    [Fact]
    public void ParseSha256For_MissingFile_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, UmuComponentProvisioner.ParseSha256For("deadbeef  a.tar.xz\n", "b.tar.xz"));
    }
}
