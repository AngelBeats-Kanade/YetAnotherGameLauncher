using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>
/// Wine 运行时发现与推荐链（umu → Proton → wine）：PATH/固定目录扫描纯逻辑 +
/// 推荐生成（prefix 统一在数据目录下，绝不写进游戏安装目录）。
/// umu/wine 路径全部显式注入，测试与 CI 双平台确定性。
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
    public void FindUmuRun_FoundInYaglInstallDir()
    {
        var umu = WriteExecutable(_home.FilePath(".local", "share", "yagl", "umu", "umu-run"));

        Assert.Equal(umu, CompatTools.FindUmuRun(pathValue: "", home: _home.Path));
    }

    [Fact]
    public void FindUmuRun_FoundOnPath()
    {
        var umu = WriteExecutable(_home.FilePath("bin", "umu-run"));

        Assert.Equal(
            umu,
            CompatTools.FindUmuRun(pathValue: _home.FilePath("bin"), home: _home.Path));
    }

    [Fact]
    public void FindUmuRun_Missing_ReturnsNull()
    {
        Assert.Null(CompatTools.FindUmuRun(pathValue: "", home: _home.Path));
    }

    [Fact]
    public void FindUmuRun_NotExecutable_LinuxReturnsNull()
    {
        // Linux 上存在的文件但没有可执行位 → 不可用；Windows 分支视为存在即可用
        var umu = _home.FilePath(".local", "share", "yagl", "umu", "umu-run");
        Directory.CreateDirectory(Path.GetDirectoryName(umu)!);
        File.WriteAllText(umu, "#!/bin/sh");

        var found = CompatTools.FindUmuRun(pathValue: "", home: _home.Path);

        Assert.Equal(OperatingSystem.IsWindows(), found is not null);
    }

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
            CompatTools.PrefixRoot(_home.Path, _dataHome.Path));
    }

    [Fact]
    public void PrefixPathFor_GameScoped()
    {
        Assert.Equal(
            _dataHome.FilePath("yagl", "prefixes", "wuthering-waves"),
            CompatTools.PrefixPathFor("wuthering-waves", _home.Path, _dataHome.Path));
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

    // ---------- umu / wine 启动配置 ----------

    [Fact]
    public void BuildUmuLaunch_SetsUmuEnvWithoutSteamCompat()
    {
        var launch = CompatTools.BuildUmuLaunch(
            "wuthering-waves", "/home/u/.local/share/yagl/umu/umu-run", _home.Path, _dataHome.Path);

        Assert.Equal(LaunchMode.Umu, launch.Mode);
        Assert.Equal("umu", launch.RuntimeName);
        Assert.EndsWith("{exe}", launch.CommandTemplate, StringComparison.Ordinal);
        Assert.Equal("umu-wuthering-waves", launch.Environment["GAMEID"]);
        Assert.Equal("umu-wuthering-waves", launch.Environment["UMU_ID"]);
        Assert.Equal(
            _dataHome.FilePath("yagl", "prefixes", "wuthering-waves"),
            launch.Environment["WINEPREFIX"]);
        Assert.False(launch.Environment.ContainsKey("STEAM_COMPAT_DATA_PATH"));
    }

    [Fact]
    public void BuildWineLaunch_SetsWinePrefix()
    {
        var launch = CompatTools.BuildWineLaunch(
            "wuthering-waves", "wine", _home.Path, _dataHome.Path);

        Assert.Equal(LaunchMode.Wine, launch.Mode);
        Assert.StartsWith("wine", launch.CommandTemplate, StringComparison.Ordinal);
        Assert.EndsWith("{exe}", launch.CommandTemplate, StringComparison.Ordinal);
        Assert.Equal(
            _dataHome.FilePath("yagl", "prefixes", "wuthering-waves"),
            launch.Environment["WINEPREFIX"]);
    }

    // ---------- 推荐链 ----------

    [Fact]
    public void BuildRecommendedLaunch_PrefersUmuOverProton()
    {
        InstallProton("GE-Proton10-9");

        var launch = CompatTools.BuildRecommendedLaunch(
            "wuthering-waves", ["GE-Proton10-9"], nvidiaGpuPresent: true,
            home: _home.Path, dataHome: _dataHome.Path,
            umuRunPath: "/x/umu-run", winePath: null);

        Assert.NotNull(launch);
        Assert.Equal(LaunchMode.Umu, launch.Mode);
        Assert.Equal("/x/umu-run {exe}", launch.CommandTemplate);
        Assert.Equal("1", launch.Environment["SteamOS"]);
        Assert.Equal("1", launch.Environment["PROTON_ENABLE_NVAPI"]);
    }

    [Fact]
    public void BuildRecommendedLaunch_ProtonWhenNoUmu()
    {
        InstallProton("GE-Proton10-9");

        var launch = CompatTools.BuildRecommendedLaunch(
            "wuthering-waves", ["GE-Proton10-9"], home: _home.Path, dataHome: _dataHome.Path);

        Assert.NotNull(launch);
        Assert.Equal(LaunchMode.Proton, launch.Mode);
        Assert.Equal("GE-Proton10-9", launch.RuntimeName);
        Assert.Contains("run {exe}", launch.CommandTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRecommendedLaunch_WineWhenNoUmuNoProton()
    {
        var launch = CompatTools.BuildRecommendedLaunch(
            "wuthering-waves", [], home: _home.Path, dataHome: _dataHome.Path,
            winePath: "wine");

        Assert.NotNull(launch);
        Assert.Equal(LaunchMode.Wine, launch.Mode);
        Assert.Contains("{exe}", launch.CommandTemplate, StringComparison.Ordinal);
        Assert.Contains("WINEPREFIX", launch.Environment.Keys);
    }

    [Fact]
    public void BuildRecommendedLaunch_NothingInstalled_StaysUmuTemplateForGuidedInstall()
    {
        // 什么都装了也没有：仍给 umu 模板（引导安装完成后即可直接启动），推荐项标记无运行时
        var launch = CompatTools.BuildRecommendedLaunch(
            "wuthering-waves", [], home: _home.Path, dataHome: _dataHome.Path);

        Assert.NotNull(launch);
        Assert.Equal(LaunchMode.Umu, launch.Mode);
        Assert.Null(launch.RuntimeName);
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
