using Xunit;
using YetAnotherGameLauncher.Core.Models;
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

/// <summary>
/// 启动选项开关 → Proton 环境变量的映射（CompatTools.FeatureEnvironment，两条启动链的单一事实源）。
/// 变量名经本机 dwproton-11.0-13 proton 脚本 grep 实证：PROTON_USE_WAYLAND/PROTON_ENABLE_WAYLAND
/// 双名映射 compat_config "wayland"（1966-1967 行）、PROTON_DLSS_UPGRADE → protonfixes.setup_upscalers
/// "dlss"（2520 行）、PROTON_LOG/PROTON_LOG_DIR 为调试日志开关。
/// </summary>
public class CompatToolsFeatureEnvironmentTests
{
    [Fact]
    public void AllOff_ProducesEmptyDictionary()
    {
        var environment = CompatTools.FeatureEnvironment(new LaunchOptions());

        Assert.Empty(environment);
    }

    [Fact]
    public void WaylandToggle_MapsProtonUseWayland()
    {
        var environment = CompatTools.FeatureEnvironment(new LaunchOptions { UseWayland = true });

        Assert.Equal("1", environment["PROTON_USE_WAYLAND"]);
    }

    [Fact]
    public void DlssToggle_MapsUpgradeAndNvapi()
    {
        // DLSS 升级依赖 NVAPI：DXVK-NVAPI 未启用时游戏拿不到 DLSS 接口，两键连带
        var environment = CompatTools.FeatureEnvironment(new LaunchOptions { UpgradeDlss = true });

        Assert.Equal("1", environment["PROTON_DLSS_UPGRADE"]);
        Assert.Equal("1", environment["PROTON_ENABLE_NVAPI"]);
    }

    [Fact]
    public void LogToggle_MapsLogAndDirectory()
    {
        var logDir = Path.Combine("data", "logs");

        var environment = CompatTools.FeatureEnvironment(
            new LaunchOptions { EnableProtonLog = true }, logDirectory: logDir);

        Assert.Equal("1", environment["PROTON_LOG"]);
        Assert.Equal(logDir, environment["PROTON_LOG_DIR"]);
    }

    [Fact]
    public void MultipleToggles_CombineIntoSingleDictionary()
    {
        var environment = CompatTools.FeatureEnvironment(new LaunchOptions
        {
            UseWayland = true,
            UpgradeDlss = true,
            EnableProtonLog = true,
        });

        Assert.Equal(4, environment.Count);
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

/// <summary>
/// Proton 版本扫描的降级纪律（VM-F5，2026-09-24）：FindProtonVersions 是
/// LaunchSettingsViewModel 构造器路径（无兜底），根目录不可读（权限/IO 故障）曾把
/// UnauthorizedAccessException 直接抛进构造器炸掉整个设置页——与 CreateLaunchError
/// 的降级纪律矛盾。不可读的根按"无此根"跳过。
/// </summary>
public class CompatToolsScanDegradationTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose()
    {
        RestoreRootPermissions();
        _home.Dispose();
    }

    /// <summary>被测根目录（.steam/steam/compatibilitytools.d，ProtonRoots 首选根）。</summary>
    private string PrimaryRoot => _home.FilePath(".steam", "steam", "compatibilitytools.d");

    private void RestoreRootPermissions()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // CA1416：SetUnixFileMode 仅 Unix；Windows 路径上无权限可恢复
        }

        if (!Directory.Exists(PrimaryRoot))
        {
            // root 前提探针的 Skip 路径先于夹具构造抛出：无根目录即无权限可恢复，
            // 对不存在路径 SetUnixFileMode 的异常会与 SkipTestException 合并成 FAIL
            return;
        }

        File.SetUnixFileMode(PrimaryRoot,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    [Fact]
    public void FindProtonVersions_UnreadableRoot_SkipsInsteadOfThrowing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("chmod 语义仅 Linux（且 root 豁免 DAC，红绿以非 root 本机为准）");
            return; // CA1416：其后代码仅 Linux 可达
        }

        // root/CAP_DAC_OVERRIDE 豁免 DAC：拒读形态构造不出（同族前提探针，FileUtilitiesTests 同款）
        if (!DacExemptionProbe.CanConstructDeniedFixture(_home.Path))
        {
            Assert.Skip("当前进程可无视权限位（root/CAP_DAC_OVERRIDE 等能力豁免），拒读形态不成立");
        }

        Directory.CreateDirectory(PrimaryRoot);
        File.WriteAllText(Path.Combine(PrimaryRoot, "GE-Proton99-99"), string.Empty);
        File.SetUnixFileMode(PrimaryRoot, UnixFileMode.None); // 拒绝包括所有者的一切访问

        // 红（修复前实测）：EnumerateDirectories 抛 UnauthorizedAccessException 穿出
        var thrown = Record.Exception(() => CompatTools.FindProtonVersions(_home.Path));

        Assert.Null(thrown);
    }

    [Fact]
    public void FindProtonWine_PicksFirstProtonBundledWine()
    {
        // hpatchz.exe 经 wine 运行的第二供给源（2026-10-02）：…/files/bin/wine 形态命中即返回；
        // home 注入隔离真机 Proton 环境
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("executable-bit semantics are Unix-only");
        }

        var tools = Path.Combine(_home.Path, ".local", "share", "Steam", "compatibilitytools.d");
        var protonWine = Path.Combine(tools, "GE-Proton-test", "files", "bin", "wine");
        Directory.CreateDirectory(Path.GetDirectoryName(protonWine)!);
        File.WriteAllText(protonWine, "#!/bin/sh\n");
        if (OperatingSystem.IsLinux()) // CA1416：SetUnixFileMode 仅 Unix
        {
            File.SetUnixFileMode(protonWine, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        Directory.CreateDirectory(Path.Combine(tools, "empty-proton", "files")); // 无 wine 的目录跳过

        Assert.Equal(protonWine, CompatTools.FindProtonWine(_home.Path));
        Assert.Null(CompatTools.FindProtonWine(Path.Combine(_home.Path, "nonexistent-home")));
    }

    [Fact]
    public void FindProtonWine_UnreadableRoot_SkipsToNextRootInsteadOfThrowing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("chmod 语义仅 Linux（且 root 豁免 DAC，红绿以非 root 本机为准）");
            return; // CA1416：其后代码仅 Linux 可达
        }

        // root/CAP_DAC_OVERRIDE 豁免 DAC：拒读形态构造不出（同族前提探针，FindProtonVersions 专测同款）
        if (!DacExemptionProbe.CanConstructDeniedFixture(_home.Path))
        {
            Assert.Skip("当前进程可无视权限位（root/CAP_DAC_OVERRIDE 等能力豁免），拒读形态不成立");
        }

        // 首根拒读 + 次根有 wine：守卫语义 = 按"无此根"跳过继续扫（姊妹 FindProtonVersions 同款）；
        // 裸枚举会让 UnauthorizedAccessException 穿出供给器，上层包装成误导性的
        // "Patch application failed: Access denied"，且首根中止会吞掉次根的命中机会
        Directory.CreateDirectory(PrimaryRoot);
        File.SetUnixFileMode(PrimaryRoot, UnixFileMode.None);

        var secondaryRoot = _home.FilePath(".local", "share", "Steam", "compatibilitytools.d");
        var wine = Path.Combine(secondaryRoot, "GE-Proton-test", "files", "bin", "wine");
        Directory.CreateDirectory(Path.GetDirectoryName(wine)!);
        File.WriteAllText(wine, "#!/bin/sh\n");
        File.SetUnixFileMode(wine, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.Equal(wine, CompatTools.FindProtonWine(_home.Path));
    }
}
