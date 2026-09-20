using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class CompatToolsTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void FindProtonVersions_ScansKnownDirs_PutsDefaultFirst()
    {
        Directory.CreateDirectory(_home.FilePath(".steam/steam/compatibilitytools.d/GE-Proton9"));
        Directory.CreateDirectory(_home.FilePath(".steam/steam/compatibilitytools.d/dw-proton"));
        Directory.CreateDirectory(_home.FilePath(".local/share/Steam/steamapps/common/SomeGame")); // 非 Proton，忽略

        var versions = CompatTools.FindProtonVersions(_home.Path);

        Assert.Equal("dw-proton", versions[0]); // 默认推荐置顶
        Assert.Contains("GE-Proton9", versions);
        Assert.DoesNotContain("SomeGame", versions);
    }

    [Fact]
    public void BuildProtonLaunch_GeneratesTemplateAndEnv()
    {
        Directory.CreateDirectory(_home.FilePath(".steam/steam/compatibilitytools.d/dw-proton"));

        // 数据根显式注入：缺省取 AppPaths.DataHomeDirectory，不随 home 推导
        var launch = CompatTools.BuildProtonLaunch(
            "wuthering-waves", "dw-proton", _home.Path, dataHome: _home.FilePath(".local", "share"));

        Assert.Contains("dw-proton", launch.CommandTemplate);
        Assert.Contains("proton\" run {exe}", launch.CommandTemplate);
        Assert.Equal(
            _home.FilePath(".local", "share", "yagl", "prefixes", "wuthering-waves"),
            launch.Environment["STEAM_COMPAT_DATA_PATH"]);
    }

    [Fact]
    public void BuildProtonLaunch_MissingVersion_FallsBackToExpectedPath()
    {
        var launch = CompatTools.BuildProtonLaunch("wuthering-waves", "dw-proton", _home.Path);

        Assert.Contains(".steam", launch.CommandTemplate);
        Assert.True(launch.Environment.ContainsKey("STEAM_COMPAT_CLIENT_INSTALL_PATH"));
    }
}

public class NumericSortKeyLongSegmentTests
{
    [Fact]
    public void NumericSortKey_SevenDigitSegment_SortsNumerically()
    {
        // 回归（2026-09-20 三审）：6 位补零对 7 位数字段会字符串错序（"1234567" < "999999"）
        Assert.True(
            string.CompareOrdinal(CompatTools.NumericSortKey("a-1234567"), CompatTools.NumericSortKey("a-999999")) > 0);
    }
}
