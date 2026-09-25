using System.Globalization;
using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 限速草稿的文化无关性（F39，artifacts/bugs.md）：解析与回显都必须用机器格式（InvariantCulture），
/// 不随线程 CurrentCulture 漂移——逗号小数文化的系统上 "0.5" 曾被 AllowThousands 当千分位静默放大 10x，
/// "1e999" 曾经 unchecked cast 变 long.MinValue 负值持久化。culture 是进程全局 → sequential 集合 + 测后还原。
/// </summary>
[Collection("sequential")]
public class SettingsCultureTests : IDisposable
{
    private readonly VmFactory.Context _ctx;
    private readonly CultureInfo _originalCulture;

    public SettingsCultureTests()
    {
        _originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        _ctx = VmFactory.Build();
    }

    public void Dispose()
    {
        _ctx.Dispose();
        CultureInfo.CurrentCulture = _originalCulture;
    }

    [Fact]
    public async Task SaveDownloadLimit_DotDecimal_InCommaCulture_ParsesFractionNotThousands()
    {
        await _ctx.Vm.InitializeAsync();
        _ctx.Vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(_ctx.Vm.CurrentPage);

        settings.SpeedLimitMbDraft = "0.5";
        await settings.SaveDownloadLimitCommand.ExecuteAsync(null);

        Assert.False(settings.SpeedLimitSave.Failed);
        var reloader = new GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.Equal(524288L, reloader.Catalog!.Settings.DownloadSpeedLimitBytes);
    }

    [Fact]
    public async Task DraftEcho_UsesInvariantDecimalPoint_EvenInCommaCulture()
    {
        await _ctx.Vm.InitializeAsync();
        _ctx.Vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(_ctx.Vm.CurrentPage);
        settings.SpeedLimitMbDraft = "1.5";
        await settings.SaveDownloadLimitCommand.ExecuteAsync(null);
        Assert.False(settings.SpeedLimitSave.Failed);

        // 重开设置页：草稿回显按机器格式（不变文化小数点），不随系统文化变逗号
        _ctx.Vm.ShowSettingsCommand.Execute(null);
        var reopened = Assert.IsType<SettingsViewModel>(_ctx.Vm.CurrentPage);
        Assert.Equal("1.5", reopened.SpeedLimitMbDraft);
    }

    [Fact]
    public async Task SaveDownloadLimit_NonFiniteInput_RejectedWithoutPersisting()
    {
        await _ctx.Vm.InitializeAsync();
        _ctx.Vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(_ctx.Vm.CurrentPage);

        settings.SpeedLimitMbDraft = "1e999";
        await settings.SaveDownloadLimitCommand.ExecuteAsync(null);

        Assert.True(settings.SpeedLimitSave.Failed);
        var reloader = new GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        var persisted = reloader.Catalog!.Settings.DownloadSpeedLimitBytes;
        Assert.True(persisted >= 0, $"persisted={persisted}");
    }
}
