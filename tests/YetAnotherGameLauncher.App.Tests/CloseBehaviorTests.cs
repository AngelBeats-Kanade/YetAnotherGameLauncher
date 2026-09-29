using Xunit;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 关闭按钮行为（settings.closeAction）的 VM 层回归：默认/配置驱动初始化、
/// 应用与持久化、设置页 radio 命令、隐藏路径的窗口尺寸持久化。
/// 窗口层拦截/唤回/真退出见 CloseToTrayHeadlessTests；默认 Exit 关窗语义见
/// MainWindowChromeHeadlessTests。
/// </summary>
[Collection("sequential")]
public class CloseBehaviorTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public CloseBehaviorTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task DefaultConfig_CloseToTrayDisabledAfterInit()
    {
        await _ctx.Vm.InitializeAsync();

        Assert.Equal(CloseAction.Exit, _ctx.Vm.CloseAction);
        Assert.False(_ctx.Vm.IsCloseToTrayEnabled);
    }

    [Fact]
    public async Task HideToTrayConfig_CloseToTrayEnabledAfterInit()
    {
        var ctx = VmFactory.Build(configJson: VmFactory.SampleConfigJson.Replace(
            "\"settings\": {",
            "\"settings\": { \"closeAction\": \"HideToTray\","));
        using var _ = ctx;

        await ctx.Vm.InitializeAsync();

        Assert.Equal(CloseAction.HideToTray, ctx.Vm.CloseAction);
        Assert.True(ctx.Vm.IsCloseToTrayEnabled);
    }

    [Fact]
    public async Task ApplyCloseActionAsync_PersistsToDiskAndFlipsFlag()
    {
        await _ctx.Vm.InitializeAsync();

        Assert.True(await _ctx.Vm.ApplyCloseActionAsync(CloseAction.HideToTray));
        Assert.True(_ctx.Vm.IsCloseToTrayEnabled);

        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.Equal(CloseAction.HideToTray, reloader.Catalog!.Settings.CloseAction);
    }

    [Fact]
    public async Task SettingsRadioCommands_ApplyAndPersistRoundTrip()
    {
        await _ctx.Vm.InitializeAsync();
        _ctx.Vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(_ctx.Vm.CurrentPage);

        // 默认 radio 态：退出应用选中
        Assert.True(settings.IsCloseActionExit);
        Assert.False(settings.IsCloseActionHide);

        await settings.SelectCloseActionHideCommand.ExecuteAsync(null);
        Assert.True(settings.IsCloseActionHide);
        Assert.False(settings.IsCloseActionExit);
        Assert.False(settings.CloseActionSave.Failed);
        Assert.NotEmpty(settings.CloseActionSave.Message);
        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.Equal(CloseAction.HideToTray, reloader.Catalog!.Settings.CloseAction);

        await settings.SelectCloseActionExitCommand.ExecuteAsync(null);
        Assert.True(settings.IsCloseActionExit);
        reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.Equal(CloseAction.Exit, reloader.Catalog!.Settings.CloseAction);
    }

    [Fact]
    public async Task OnWindowHiddenToTray_PersistsDimensions()
    {
        await _ctx.Vm.InitializeAsync();

        _ctx.Vm.OnWindowHiddenToTray(1234, 567, false);

        var settings = _ctx.CatalogService.Catalog!.Settings;
        Assert.Equal(1234, settings.WindowWidth);
        Assert.Equal(567, settings.WindowHeight);
        Assert.False(settings.WindowMaximized);
    }
}
