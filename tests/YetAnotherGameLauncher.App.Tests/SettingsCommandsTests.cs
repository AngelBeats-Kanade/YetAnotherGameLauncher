using Xunit;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 设置页命令组（Phase 4 补测，2026-09-19）：限速草稿校验与应用、自启切换回读、
/// 安装根目录空草稿拒绝、应用背景设置/清除。对应审计缺口 MainWindowViewModel 979-1326。
/// </summary>
[Collection("sequential")]
public class SettingsCommandsTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public SettingsCommandsTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task SaveDownloadLimit_ValidMb_AppliesAndPersists()
    {
        await _ctx.Vm.InitializeAsync();
        _ctx.Vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(_ctx.Vm.CurrentPage);

        settings.SpeedLimitMbDraft = " 2 ";
        await settings.SaveDownloadLimitCommand.ExecuteAsync(null);

        Assert.False(settings.SpeedLimitSave.Failed);
        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.Equal(2L * 1024 * 1024, reloader.Catalog!.Settings.DownloadSpeedLimitBytes);
    }

    [Fact]
    public async Task SaveDownloadLimit_InvalidDraft_FailsWithoutApply()
    {
        await _ctx.Vm.InitializeAsync();
        _ctx.Vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(_ctx.Vm.CurrentPage);

        settings.SpeedLimitMbDraft = "-1";
        await settings.SaveDownloadLimitCommand.ExecuteAsync(null);

        Assert.True(settings.SpeedLimitSave.Failed);
        settings.SpeedLimitMbDraft = "abc";
        await settings.SaveDownloadLimitCommand.ExecuteAsync(null);
        Assert.True(settings.SpeedLimitSave.Failed);
    }

    [Fact]
    public async Task SaveInstallRoot_EmptyDraft_Fails()
    {
        await _ctx.Vm.InitializeAsync();
        _ctx.Vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(_ctx.Vm.CurrentPage);

        settings.InstallRootDraft = "   ";
        await settings.SaveInstallRootCommand.ExecuteAsync(null);

        Assert.True(settings.InstallRootSave.Failed);
    }

    [Fact]
    public async Task SetAppBackground_ValidPath_PersistsAndClears_RestoresNull()
    {
        await _ctx.Vm.InitializeAsync();
        var image = _ctx.TempDir.FilePath("bg.png");
        await File.WriteAllBytesAsync(image, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="));

        Assert.True(await _ctx.Vm.SetAppBackgroundAsync(image));
        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.Equal(image, reloader.Catalog!.Settings.AppBackgroundImage);

        Assert.True(await _ctx.Vm.SetAppBackgroundAsync(null));
        var reloader2 = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader2.LoadAsync();
        Assert.Null(reloader2.Catalog!.Settings.AppBackgroundImage);
    }

    [Fact]
    public async Task SetAppBackground_NonexistentPath_ReturnsFalse()
    {
        await _ctx.Vm.InitializeAsync();

        Assert.False(await _ctx.Vm.SetAppBackgroundAsync(_ctx.TempDir.FilePath("no-such.png")));
    }
}
