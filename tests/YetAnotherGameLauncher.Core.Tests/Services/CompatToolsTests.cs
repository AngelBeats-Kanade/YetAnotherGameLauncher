using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>Proton 兼容层工具：版本扫描/推荐挑选/启动命令生成的纯逻辑。</summary>
public sealed class ProtonCompatTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    private void InstallProton(params string[] names)
    {
        var dir = _home.FilePath(".steam", "steam", "compatibilitytools.d");
        Directory.CreateDirectory(dir);
        foreach (var name in names)
        {
            Directory.CreateDirectory(Path.Combine(dir, name));
        }
    }

    [Fact]
    public void FindProtonVersions_ListsCompatibilityToolsDirs()
    {
        InstallProton("GE-Proton10-9", "dw-proton", "SteamLinuxRuntime");

        var versions = CompatTools.FindProtonVersions(_home.Path);

        // compatibilitytools.d 里全部条目都算兼容工具；默认推荐版本置顶
        Assert.Equal(["dw-proton", "GE-Proton10-9", "SteamLinuxRuntime"], versions);
    }

    [Fact]
    public void FindProtonVersions_CommonRoot_KeepsOnlyProtonPrefixed()
    {
        // Steam 自带运行时目录里非 Proton* 的条目（如游戏本体目录）不算兼容层
        var common = _home.FilePath(".steam", "root", "steamapps", "common");
        Directory.CreateDirectory(Path.Combine(common, "Proton Hotfix"));
        Directory.CreateDirectory(Path.Combine(common, "Some Game"));

        var versions = CompatTools.FindProtonVersions(_home.Path);

        Assert.Equal(["Proton Hotfix"], versions);
    }

    [Fact]
    public void FindProtonVersions_MissingHome_ReturnsEmpty()
    {
        Assert.Empty(CompatTools.FindProtonVersions(_home.FilePath("nonexistent")));
    }

    [Fact]
    public void PickRecommendedProton_GeProtonWinsOverEverything()
    {
        var picked = CompatTools.PickRecommendedProton(["Proton Hotfix", "dw-proton", "GE-Proton10-9", "GE-Proton10-31"]);

        Assert.Equal("GE-Proton10-31", picked); // GE 字典序最新
    }

    [Fact]
    public void PickRecommendedProton_Empty_ReturnsNull()
    {
        Assert.Null(CompatTools.PickRecommendedProton([]));
    }

    [Fact]
    public void BuildProtonLaunch_QuotesProtonAndSetsCompatEnv()
    {
        InstallProton("GE-Proton10-9");

        var (command, environment) = CompatTools.BuildProtonLaunch("GE-Proton10-9", _home.Path);

        Assert.StartsWith("\"", command);
        Assert.Contains("GE-Proton10-9", command, StringComparison.Ordinal);
        Assert.Contains("run {exe}", command, StringComparison.Ordinal);
        Assert.Equal("{installDir}/compatdata", environment["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(Path.Combine(_home.Path, ".steam", "steam"),
            environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"]);
    }

    [Fact]
    public void LocateProton_FindsExistingVersion()
    {
        InstallProton("GE-Proton10-9");

        var located = CompatTools.LocateProton("GE-Proton10-9", _home.Path);

        Assert.Equal(_home.FilePath(".steam", "steam", "compatibilitytools.d", "GE-Proton10-9"), located);
        Assert.Null(CompatTools.LocateProton("GE-Proton99-99", _home.Path));
    }

    [Fact]
    public void RecommendedEnvironment_Wuthering_PretendsSteamOs()
    {
        var environment = CompatTools.RecommendedEnvironment("wuthering-waves");

        Assert.Equal("1", environment["SteamOS"]); // NVIDIA 分支仅在 Linux + NVIDIA 显卡时追加
        Assert.Empty(CompatTools.RecommendedEnvironment("arknights-endfield"));
    }
}
