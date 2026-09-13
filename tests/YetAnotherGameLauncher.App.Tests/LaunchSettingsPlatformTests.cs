using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>启动设置卡：启动方式仅 umu/直接运行两项（umu 仅 Linux 面板展示）；
/// Linux 推荐 umu 启动（+SteamOS/NVAPI+默认 DW-Proton 代号），Proton 发行版切换写入 PROTONPATH。</summary>
public class LaunchSettingsPlatformTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public LaunchSettingsPlatformTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task LinuxMode_RecommendsNativeUmuWithSteamOsAndNvapi()
    {
        await _ctx.Vm.InitializeAsync();
        var platform = new FakePlatformInfo(isLinux: true, nvidiaGpuPresent: true);
        var game = _ctx.Vm.Games[0];

        var launchSettings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: platform, protonVersions: ["GE-Proton10-9", "dw-proton"]);

        Assert.True(launchSettings.IsLinux);
        Assert.Equal(
            [LaunchMode.NativeUmu, LaunchMode.Direct],
            launchSettings.LaunchModes.Select(m => m.Mode));
        Assert.Equal(LaunchMode.NativeUmu, launchSettings.SelectedLaunchMode?.Mode);
        Assert.Contains("native-umu", launchSettings.CommandTemplate, StringComparison.Ordinal);
        Assert.Contains("SteamOS=1", launchSettings.EnvironmentText, StringComparison.Ordinal);
        Assert.Contains("PROTON_ENABLE_NVAPI=1", launchSettings.EnvironmentText, StringComparison.Ordinal);
        Assert.Equal("DW-Proton", launchSettings.SelectedProtonFlavor);
        Assert.Contains("PROTONPATH=DW-Proton", launchSettings.EnvironmentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsMode_StaysDirectWithoutCompatEnv()
    {
        await _ctx.Vm.InitializeAsync();
        var platform = new FakePlatformInfo(isLinux: false);
        var game = _ctx.Vm.Games[0];

        var launchSettings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: platform);

        Assert.False(launchSettings.IsLinux);
        Assert.Equal(LaunchMode.Direct, launchSettings.SelectedLaunchMode?.Mode);
        Assert.Equal("{exe}", launchSettings.CommandTemplate);
        Assert.DoesNotContain("SteamOS", launchSettings.EnvironmentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxLegacyTemplate_ShowsUmuModeWithoutRewritingDraft()
    {
        // 存量 wine/umu-run/自定义模板：下拉仅作 umu 显示映射，草稿不被重写，
        // 用户主动切换启动方式前模板照旧运行
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        game.Game.Launch.CommandTemplate = "wine {exe}";

        var launchSettings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"]);

        Assert.Equal(LaunchMode.NativeUmu, launchSettings.SelectedLaunchMode?.Mode);
        Assert.Equal("wine {exe}", launchSettings.CommandTemplate);
        Assert.DoesNotContain("GAMEID", launchSettings.EnvironmentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtonFlavorChange_RewritesProtonPathCodename()
    {
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var launchSettings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"]);

        launchSettings.SelectedProtonFlavor = "GE-Proton";

        Assert.Contains("PROTONPATH=GE-Proton", launchSettings.EnvironmentText, StringComparison.Ordinal);
        Assert.DoesNotContain("PROTONPATH=DW-Proton", launchSettings.EnvironmentText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/opt/protons/dwproton-11.0-12", "DW-Proton")]
    [InlineData("GE-Proton10-9", "GE-Proton")]
    [InlineData("UMU-Proton", "UMU-Proton")]
    [InlineData("/home/u/.local/share/Steam/compatibilitytools.d/GE-Proton10-9", "GE-Proton")]
    [InlineData("", "DW-Proton")]
    public async Task ProtonFlavor_DetectedFromExistingProtonPath(string protonPath, string expected)
    {
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        if (protonPath.Length > 0)
        {
            game.Game.Launch.Environment["PROTONPATH"] = protonPath;
        }

        var launchSettings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"]);

        Assert.Equal(expected, launchSettings.SelectedProtonFlavor);
    }
}
