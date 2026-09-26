using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests.Services;

/// <summary>
/// 依赖安装目标解析决策表：Direct / 原生 umu（Proton 定位、版本择新、prefix 未初始化）/
/// 自定义 WINEPREFIX（系统 wine）/ 生成的 WINEPREFIX 不算自定义。
/// </summary>
public class WinePrefixTargetResolverTests : IDisposable
{
    private readonly TempDir _temp = new();

    private string DataHome => _temp.FilePath("data-home");

    private string CompatRoot => _temp.FilePath("data-home", "Steam", "compatibilitytools.d");

    /// <summary>umu 布局的统一 prefix 根（STEAM_COMPAT_DATA_PATH）。</summary>
    private string CompatData => _temp.FilePath("data-home", "yagl", "prefixes", "wuthering-waves");

    private string SystemWine { get; }

    public WinePrefixTargetResolverTests()
    {
        SystemWine = CreateExecutable(_temp.FilePath("usr", "bin", "wine"));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>造一个"可执行"文件（Linux 补执行位）。</summary>
    private string CreateExecutable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>造一个已就绪的本地 Proton（含 files/bin/wine）。</summary>
    private string CreateProton(string dirName)
    {
        var dir = Path.Combine(CompatRoot, dirName);
        CreateExecutable(Path.Combine(dir, "files", "bin", "wine"));
        return dir;
    }

    /// <summary>初始化 umu prefix（pfx 链接下的 drive_c；pfx→. 由 UmuPrefix.Setup 建立，测试直接建真实目录链等价物）。</summary>
    private void InitUmuPrefix()
    {
        Directory.CreateDirectory(Path.Combine(CompatData, "pfx", "drive_c", "windows"));
    }

    [Fact]
    public void DirectMode_Unavailable()
    {
        var resolution = WinePrefixTargetResolver.Resolve(
            LaunchMode.Direct, "wuthering-waves", null, null, SystemWine, DataHome);

        Assert.Null(resolution.Target);
        Assert.Equal(WineTargetUnavailable.DirectMode, resolution.Reason);
    }

    [Fact]
    public void UmuMode_ExactCodenameDir_ResolvesProtonWine()
    {
        var protonDir = CreateProton("dwproton-11.0-12");
        InitUmuPrefix();

        var resolution = WinePrefixTargetResolver.Resolve(
            LaunchMode.NativeUmu, "wuthering-waves", null, "DW-Proton", SystemWine, DataHome);

        Assert.Null(resolution.Reason);
        var target = resolution.Target!;
        Assert.Equal(Path.Combine(protonDir, "files", "bin", "wine"), target.WineExecutable);
        Assert.Equal(Path.Combine(CompatData, "pfx"), target.WinePrefixDirectory);
        Assert.Equal(
            Path.Combine(CompatData, "pfx", "drive_c", "windows", "Fonts"),
            target.FontsDirectory);
        Assert.Equal(Path.Combine(CompatData, ".yagl-deps"), target.StateDirectory);
    }

    [Fact]
    public void UmuMode_FlavorPrefixScan_PicksNewestByNaturalSort()
    {
        CreateProton("dwproton-9-1");
        var newest = CreateProton("dwproton-11.0-12");
        InitUmuPrefix();

        var resolution = WinePrefixTargetResolver.Resolve(
            LaunchMode.NativeUmu, "wuthering-waves", null, "DW-Proton", SystemWine, DataHome);

        Assert.Equal(Path.Combine(newest, "files", "bin", "wine"), resolution.Target!.WineExecutable);
    }

    [Fact]
    public void UmuMode_ProtonPathEnv_AbsoluteDirWinsOverFlavorScan()
    {
        CreateProton("dwproton-11.0-12");
        var custom = CreateProton("dwproton-99-0-custom");
        Directory.CreateDirectory(_temp.FilePath("custom-proton-root"));
        var absolute = CreateExecutable(Path.Combine(custom, "files", "bin", "wine"));
        InitUmuPrefix();

        var resolution = WinePrefixTargetResolver.Resolve(
            LaunchMode.NativeUmu, "wuthering-waves",
            new Dictionary<string, string> { ["PROTONPATH"] = custom },
            "DW-Proton", SystemWine, DataHome);

        Assert.Equal(absolute, resolution.Target!.WineExecutable);
    }

    [Fact]
    public void UmuMode_NoLocalProton_Unavailable()
    {
        var resolution = WinePrefixTargetResolver.Resolve(
            LaunchMode.NativeUmu, "wuthering-waves", null, "DW-Proton", SystemWine, DataHome);

        Assert.Null(resolution.Target);
        Assert.Equal(WineTargetUnavailable.ProtonMissing, resolution.Reason);
    }

    [Fact]
    public void UmuMode_ProtonWineBinaryMissing_Unavailable()
    {
        var dir = Path.Combine(CompatRoot, "dwproton-11.0-12");
        Directory.CreateDirectory(dir); // 目录在但没解包完整（缺 files/bin/wine）
        InitUmuPrefix();

        var resolution = WinePrefixTargetResolver.Resolve(
            LaunchMode.NativeUmu, "wuthering-waves", null, "DW-Proton", SystemWine, DataHome);

        Assert.Equal(WineTargetUnavailable.ProtonMissing, resolution.Reason);
    }

    [Fact]
    public void UmuMode_PrefixNotInitialized_Unavailable()
    {
        CreateProton("dwproton-11.0-12");
        // 不建 drive_c：首启前依赖不可装

        var resolution = WinePrefixTargetResolver.Resolve(
            LaunchMode.NativeUmu, "wuthering-waves", null, "DW-Proton", SystemWine, DataHome);

        Assert.Equal(WineTargetUnavailable.PrefixMissing, resolution.Reason);
    }

    [Fact]
    public void UmuMode_GeneratedWinePrefixEnv_NotTreatedAsCustom()
    {
        // BuildNativeUmuLaunch 生成的 WINEPREFIX 指向统一 prefix 根——必须走 umu 分支
        // （WINEPREFIX=pfx），不能被当成自定义 prefix 用系统 wine 覆盖
        CreateProton("dwproton-11.0-12");
        InitUmuPrefix();

        var resolution = WinePrefixTargetResolver.Resolve(
            LaunchMode.NativeUmu, "wuthering-waves",
            new Dictionary<string, string> { ["WINEPREFIX"] = CompatData },
            "DW-Proton", SystemWine, DataHome);

        Assert.Equal(Path.Combine(CompatData, "pfx"), resolution.Target!.WinePrefixDirectory);
    }

    [Fact]
    public void CustomWinePrefixEnv_UsesSystemWineOnGivenPrefix()
    {
        var customPrefix = _temp.FilePath("games", "wuwa-prefix");
        Directory.CreateDirectory(Path.Combine(customPrefix, "drive_c", "windows"));

        var resolution = WinePrefixTargetResolver.Resolve(
            LaunchMode.Wine, "wuthering-waves",
            new Dictionary<string, string> { ["WINEPREFIX"] = customPrefix },
            null, SystemWine, DataHome);

        Assert.Null(resolution.Reason);
        var target = resolution.Target!;
        Assert.Equal(SystemWine, target.WineExecutable);
        Assert.Equal(customPrefix, target.WinePrefixDirectory);
        Assert.Equal(Path.Combine(customPrefix, ".yagl-deps"), target.StateDirectory);
    }

    [Fact]
    public void CustomWinePrefixEnv_SystemWineMissing_Unavailable()
    {
        var customPrefix = _temp.FilePath("games", "wuwa-prefix");
        Directory.CreateDirectory(Path.Combine(customPrefix, "drive_c"));

        var resolution = WinePrefixTargetResolver.Resolve(
            LaunchMode.Wine, "wuthering-waves",
            new Dictionary<string, string> { ["WINEPREFIX"] = customPrefix },
            null, systemWinePath: null, DataHome);

        Assert.Equal(WineTargetUnavailable.WineMissing, resolution.Reason);
    }

    [Fact]
    public void WineMode_NoProtonNoCustomPrefix_SystemWineOnUnifiedPrefix()
    {
        Directory.CreateDirectory(Path.Combine(CompatData, "drive_c", "windows"));

        var resolution = WinePrefixTargetResolver.Resolve(
            LaunchMode.Wine, "wuthering-waves", null, null, SystemWine, DataHome);

        Assert.Null(resolution.Reason);
        var target = resolution.Target!;
        Assert.Equal(SystemWine, target.WineExecutable);
        Assert.Equal(CompatData, target.WinePrefixDirectory);
        Assert.Equal(Path.Combine(CompatData, ".yagl-deps"), target.StateDirectory);
    }
}
