using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>
/// Wine 运行时发现与推荐链（原生 umu → Proton → wine）：PATH 扫描纯逻辑 +
/// 推荐生成（prefix 统一在数据目录下，绝不写进游戏安装目录）。
/// wine 路径显式注入，测试与 CI 双平台确定性。
/// </summary>
public sealed class UmuWineCompatTests : IDisposable
{
    private readonly TempDir _home = new();
    private readonly TempDir _dataHome = new();

    public void Dispose()
    {
        _home.Dispose();
        _dataHome.Dispose();
    }

    // ---------- 发现 ----------

    [Fact]
    public void FindSystemWine_FoundOnPath()
    {
        var wine = WriteExecutable(_home.FilePath("bin", "wine"));

        Assert.Equal(
            wine,
            CompatTools.FindSystemWine(pathValue: _home.FilePath("bin"), home: _home.Path));
    }

    [Fact]
    public void FindSystemWine_Missing_ReturnsNull()
    {
        Assert.Null(CompatTools.FindSystemWine(pathValue: "", home: _home.Path));
    }

    [Fact]
    public void FindLutrisWineVersions_ListsRunnerDirs()
    {
        var runners = _home.FilePath(".local", "share", "lutris", "runners", "wine");
        Directory.CreateDirectory(Path.Combine(runners, "wine-ge-8-26", "bin"));
        Directory.CreateDirectory(Path.Combine(runners, "lutris-7.2", "bin"));
        Directory.CreateDirectory(Path.Combine(runners, "broken-runner")); // 无 bin/ 不算

        var versions = CompatTools.FindLutrisWineVersions(_home.Path);

        Assert.Equal(["lutris-7.2", "wine-ge-8-26"], versions);
    }

    // ---------- Prefix 统一位置 ----------

    [Fact]
    public void PrefixRoot_UnderDataHomeYagl()
    {
        Assert.Equal(
            _dataHome.FilePath("yagl", "prefixes"),
            CompatTools.PrefixRoot(_dataHome.Path));
    }

    [Fact]
    public void PrefixPathFor_GameScoped()
    {
        Assert.Equal(
            _dataHome.FilePath("yagl", "prefixes", "wuthering-waves"),
            CompatTools.PrefixPathFor("wuthering-waves", _dataHome.Path));
    }

    [Fact]
    public void BuildProtonLaunch_PrefixLivesOutsideInstallDir()
    {
        InstallProton("GE-Proton10-9");

        var launch = CompatTools.BuildProtonLaunch(
            "wuthering-waves", "GE-Proton10-9", _home.Path, _dataHome.Path);

        Assert.Equal(LaunchMode.Proton, launch.Mode);
        Assert.Equal(
            _dataHome.FilePath("yagl", "prefixes", "wuthering-waves"),
            launch.Environment["STEAM_COMPAT_DATA_PATH"]);
        // Steam 客户端根目录照旧（proton 脚本需要），prefix 数据则完全脱离游戏安装目录
        Assert.Equal(
            Path.Combine(_home.Path, ".steam", "steam"),
            launch.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"]);
    }

    // ---------- wine 启动配置 ----------

    [Fact]
    public void BuildWineLaunch_SetsWinePrefix()
    {
        var launch = CompatTools.BuildWineLaunch(
            "wuthering-waves", "wine", _dataHome.Path);

        Assert.Equal(LaunchMode.Wine, launch.Mode);
        Assert.StartsWith("wine", launch.CommandTemplate, StringComparison.Ordinal);
        Assert.EndsWith("{exe}", launch.CommandTemplate, StringComparison.Ordinal);
        Assert.Equal(
            _dataHome.FilePath("yagl", "prefixes", "wuthering-waves"),
            launch.Environment["WINEPREFIX"]);
    }

    // ---------- 推荐链 ----------

    [Fact]
    public void BuildRecommendedLaunch_ProtonFallback_WhenNativeDisabled()
    {
        InstallProton("GE-Proton10-9");

        var launch = CompatTools.BuildRecommendedLaunch(
            "wuthering-waves", ["GE-Proton10-9"], home: _home.Path, dataHome: _dataHome.Path,
            preferNativeUmu: false);

        Assert.NotNull(launch);
        Assert.Equal(LaunchMode.Proton, launch.Mode);
        Assert.Equal("GE-Proton10-9", launch.RuntimeName);
        Assert.Contains("run {exe}", launch.CommandTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRecommendedLaunch_WineFallback_WhenNativeDisabledAndNoProton()
    {
        var launch = CompatTools.BuildRecommendedLaunch(
            "wuthering-waves", [], home: _home.Path, dataHome: _dataHome.Path,
            winePath: "wine", preferNativeUmu: false);

        Assert.NotNull(launch);
        Assert.Equal(LaunchMode.Wine, launch.Mode);
        Assert.Contains("{exe}", launch.CommandTemplate, StringComparison.Ordinal);
        Assert.Contains("WINEPREFIX", launch.Environment.Keys);
    }

    [Fact]
    public void BuildRecommendedLaunch_NothingInstalled_StaysNativeUmuTemplate()
    {
        // 什么都装了也没有：默认原生 umu 模板（启动时自动下载 Proton/Runtime）
        var launch = CompatTools.BuildRecommendedLaunch(
            "wuthering-waves", [], home: _home.Path, dataHome: _dataHome.Path);

        Assert.NotNull(launch);
        Assert.Equal(LaunchMode.NativeUmu, launch.Mode);
        Assert.Equal("native-umu {exe}", launch.CommandTemplate);
    }

    [Fact]
    public void IsGeneratedEnvironmentKey_CoversCompatAndUmuKeys()
    {
        Assert.True(CompatTools.IsGeneratedEnvironmentKey("STEAM_COMPAT_DATA_PATH"));
        Assert.True(CompatTools.IsGeneratedEnvironmentKey("GAMEID"));
        Assert.True(CompatTools.IsGeneratedEnvironmentKey("UMU_ID"));
        Assert.True(CompatTools.IsGeneratedEnvironmentKey("WINEPREFIX"));
        Assert.True(CompatTools.IsGeneratedEnvironmentKey("PROTONPATH"));
        Assert.False(CompatTools.IsGeneratedEnvironmentKey("LANG"));
    }

    private void InstallProton(string name)
    {
        var dir = _home.FilePath(".steam", "steam", "compatibilitytools.d", name);
        Directory.CreateDirectory(dir);
    }

    private string WriteExecutable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
