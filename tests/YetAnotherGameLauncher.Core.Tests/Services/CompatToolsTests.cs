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

        var launch = CompatTools.BuildProtonLaunch("wuthering-waves", "GE-Proton10-9", _home.Path);

        Assert.StartsWith("\"", launch.CommandTemplate);
        Assert.Contains("GE-Proton10-9", launch.CommandTemplate, StringComparison.Ordinal);
        Assert.Contains("run {exe}", launch.CommandTemplate, StringComparison.Ordinal);
        Assert.Equal(
            _home.FilePath(".local", "share", "yagl", "prefixes", "wuthering-waves"),
            launch.Environment["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(Path.Combine(_home.Path, ".steam", "steam"),
            launch.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"]);
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

    [Fact]
    public void BuildRecommendedLaunch_MergesCompatEnvAndRecommendations()
    {
        InstallProton("GE-Proton10-9");

        var launch = CompatTools.BuildRecommendedLaunch(
            "wuthering-waves", ["GE-Proton10-9"], nvidiaGpuPresent: true, home: _home.Path,
            preferNativeUmu: false);

        Assert.NotNull(launch);
        Assert.Equal(LaunchMode.Proton, launch.Mode);
        Assert.Equal("GE-Proton10-9", launch.RuntimeName);
        Assert.Contains("run {exe}", launch.CommandTemplate, StringComparison.Ordinal);
        Assert.Equal("1", launch.Environment["SteamOS"]);
        Assert.Equal("1", launch.Environment["PROTON_ENABLE_NVAPI"]);
    }

    [Fact]
    public void BuildRecommendedLaunch_NoVersions_FallsBackToNativeUmuTemplate()
    {
        // 什么运行时都没有：仍返回原生 umu 模板，启动时由组件准备器自动下载
        var launch = CompatTools.BuildRecommendedLaunch("wuthering-waves", [], home: _home.Path);

        Assert.Equal(LaunchMode.NativeUmu, launch.Mode);
        Assert.Equal("native-umu {exe}", launch.CommandTemplate);
    }
}
