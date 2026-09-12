using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>启动设置卡的平台双模式：Linux 推荐原生 umu（+ NVIDIA 环境变量）、Windows 直连（平台注入可控）。</summary>
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
        Assert.Equal(LaunchMode.NativeUmu, launchSettings.SelectedLaunchMode?.Mode);
        Assert.Contains("native-umu", launchSettings.CommandTemplate, StringComparison.Ordinal);
        Assert.Contains("SteamOS=1", launchSettings.EnvironmentText, StringComparison.Ordinal);
        Assert.Contains("PROTON_ENABLE_NVAPI=1", launchSettings.EnvironmentText, StringComparison.Ordinal);
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
        Assert.False(launchSettings.IsProtonMode);
        Assert.Equal("{exe}", launchSettings.CommandTemplate);
        Assert.DoesNotContain("SteamOS", launchSettings.EnvironmentText, StringComparison.Ordinal);
    }
}
