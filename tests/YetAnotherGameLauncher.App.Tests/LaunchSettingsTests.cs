using Xunit;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;

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
        Assert.Empty(_ctx.Vm.Toasts); // 校验失败只走页内红字，不弹轻提示

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

    [Fact]
    public async Task Save_WithActualChange_RaisesToastListingChangedField()
    {
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var settings = game.LaunchSettings;

        settings.CommandTemplate = "wine {exe}";
        await settings.SaveCommand.ExecuteAsync(null);

        Assert.False(settings.Save.Failed);
        var toast = Assert.Single(_ctx.Vm.Toasts);
        Assert.Equal(game.DisplayName, toast.Title);
        Assert.Equal("已更新：命令模板", toast.Message);
        Assert.Equal(ToastKind.Success, toast.Kind);
    }

    [Fact]
    public async Task Save_WithMultipleChanges_JoinsLabelsInFixedOrder()
    {
        await _ctx.Vm.InitializeAsync();
        var settings = _ctx.Vm.Games[0].LaunchSettings;

        // 多字段变更：标签按固定顺序经 common_comma 连接——锁住分隔符键与排列（防键缺失回退成键名）
        settings.CommandTemplate = "wine {exe}";
        settings.EnvironmentText = "LANG=zh_CN.UTF-8";
        await settings.SaveCommand.ExecuteAsync(null);

        Assert.False(settings.Save.Failed);
        var toast = Assert.Single(_ctx.Vm.Toasts);
        Assert.Equal("已更新：命令模板、环境变量", toast.Message);
    }

    [Fact]
    public async Task Save_WithoutChanges_DoesNotRaiseToast()
    {
        await _ctx.Vm.InitializeAsync();
        var settings = _ctx.Vm.Games[0].LaunchSettings;

        // 草稿原样保存：页内消息槽照常提示成功，但不弹轻提示（没有实际变更）
        await settings.SaveCommand.ExecuteAsync(null);

        Assert.False(settings.Save.Failed);
        Assert.Equal("启动设置已保存", settings.Save.Message);
        Assert.Empty(_ctx.Vm.Toasts);
    }

    [Fact]
    public async Task ProtonFlavorSwitch_PreservesHalfTypedEnvironmentLine()
    {
        // 回归（2026-09-20）：切发行版即时保存曾把编辑框按"解析→序列化"重写，
        // 用户输入到一半、还没有 "=" 的半行被无声吞掉
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var settings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"]);
        await settings.SaveCommand.ExecuteAsync(null); // 先落盘构造期推荐配置，切发行版才算"即时保存"

        settings.EnvironmentText = "SAVED_KEY=1\nWINEDLLOVERRIDES";
        settings.SelectedProtonFlavor = "GE-Proton";

        // 半行原样保留、PROTONPATH 写入且已有键不动
        Assert.Contains("WINEDLLOVERRIDES", settings.EnvironmentText, StringComparison.Ordinal);
        Assert.Contains("PROTONPATH=GE-Proton", settings.EnvironmentText, StringComparison.Ordinal);
        Assert.Contains("SAVED_KEY=1", settings.EnvironmentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtonFlavorSwitch_ImmediateSave_RaisesFlavorToast()
    {
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var settings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"]);

        // 先把构造期生成的推荐配置落盘（该保存也会弹提示），再切发行版：
        // 第二次保存的差异只剩 PROTONPATH，提示须按 UI 词汇报"Proton 发行版"而非笼统的"环境变量"
        await settings.SaveCommand.ExecuteAsync(null);
        settings.SelectedProtonFlavor = "GE-Proton";
        // 发行版"选择即保存"由 fire-and-forget 任务完成：轮询等待第二条提示出现
        for (var i = 0; i < 100 && _ctx.Vm.Toasts.Count < 2; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(_ctx.Vm.Toasts.Count >= 2, "发行版切换后的即时保存应弹出第二条轻提示");
        var toast = _ctx.Vm.Toasts[^1];
        Assert.Equal("已更新：Proton 发行版", toast.Message);
        Assert.Equal(ToastKind.Success, toast.Kind);
    }
}
