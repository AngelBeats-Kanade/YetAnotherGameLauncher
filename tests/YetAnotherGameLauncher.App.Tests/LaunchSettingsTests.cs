using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>启动设置卡：编辑保存回 games.json 与校验提示。</summary>
[Collection("sequential")]
public class LaunchSettingsTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public LaunchSettingsTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task Save_UpdatesGameLaunchAndConfigFile()
    {
        await _ctx.Vm.InitializeAsync();
        var settings = _ctx.Vm.Games[0].LaunchSettings;

        settings.CommandTemplate = "wine {exe}";
        settings.WorkingDirectory = "{installDir}";
        settings.EnvironmentText = "WINEPREFIX=/tmp/pfx\nLANG=zh_CN.UTF-8";
        await settings.SaveCommand.ExecuteAsync(null);

        Assert.False(settings.Save.Failed);
        Assert.Equal("启动设置已保存", settings.Save.Message);

        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        var launch = reloader.Catalog!.Games[0].Launch;
        Assert.Equal("wine {exe}", launch.CommandTemplate);
        Assert.Equal("/tmp/pfx", launch.Environment["WINEPREFIX"]);
        Assert.Equal("zh_CN.UTF-8", launch.Environment["LANG"]);
    }

    [Fact]
    public async Task Save_InvalidEnvironmentLine_ShowsErrorWithoutSaving()
    {
        await _ctx.Vm.InitializeAsync();
        var settings = _ctx.Vm.Games[0].LaunchSettings;
        settings.EnvironmentText = "NOT-A-PAIR";

        await settings.SaveCommand.ExecuteAsync(null);

        Assert.True(settings.Save.Failed);
        Assert.Contains("NOT-A-PAIR", settings.Save.Message);

        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.Empty(reloader.Catalog!.Games[0].Launch.Environment);
    }

    [Fact]
    public async Task Save_EmptyCommandTemplate_ShowsError()
    {
        await _ctx.Vm.InitializeAsync();
        var settings = _ctx.Vm.Games[0].LaunchSettings;
        settings.CommandTemplate = "  ";

        await settings.SaveCommand.ExecuteAsync(null);

        Assert.True(settings.Save.Failed);
        Assert.Equal("命令模板不能为空", settings.Save.Message);
    }

    [Fact]
    public async Task InitialValues_ComeFromGameLaunch()
    {
        await _ctx.Vm.InitializeAsync();

        var settings = _ctx.Vm.Games[0].LaunchSettings;
        Assert.Equal("{exe}", settings.CommandTemplate);
        Assert.Equal("{installDir}", settings.WorkingDirectory);
        Assert.Equal("", settings.EnvironmentText.Trim());
    }

    [Fact]
    public async Task BrowseInstallDir_PicksFolder_NormalizesAndSaves()
    {
        var picker = new FakeFilePicker { FolderResult = @"E:\Games\Endfield" };
        using var ctx = VmFactory.Build(filePicker: picker);
        await ctx.Vm.InitializeAsync();
        var settings = ctx.Vm.Games[0].LaunchSettings;
        var draftBefore = settings.InstallDirDraft;

        await settings.BrowseInstallDirCommand.ExecuteAsync(null);

        // 选中路径反斜杠规范化为正斜杠并触发保存（启动设置整卡保存）
        Assert.Equal("E:/Games/Endfield", settings.InstallDirDraft);
        Assert.False(settings.Save.Failed);
        Assert.Equal("启动设置已保存", settings.Save.Message);
        // 起始位置为选择前的草稿（初始草稿是解析后的本机斜杠绝对路径）
        Assert.Equal(draftBefore, picker.LastFolderCall?.SuggestedPath);
        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.Equal("E:/Games/Endfield", reloader.Catalog!.Games[0].InstallDir);
    }

    [Fact]
    public async Task BrowseInstallDir_Cancel_LeavesDraftUntouched()
    {
        var picker = new FakeFilePicker { FolderResult = null };
        using var ctx = VmFactory.Build(filePicker: picker);
        await ctx.Vm.InitializeAsync();
        var settings = ctx.Vm.Games[0].LaunchSettings;

        await settings.BrowseInstallDirCommand.ExecuteAsync(null);

        Assert.False(settings.Save.HasMessage);
        Assert.False(settings.Save.Failed);
    }
}
