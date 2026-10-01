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
    public async Task ResourceQuality_DraftSaveAndReset()
    {
        // 鸣潮资源包档位（2026-10-02）：未设置 → 默认档（空串 = 跟随游戏内设置）；
        // 改选点亮脏标 → 保存落 games.json 且轻提示归入"启动选项"组；改回默认档落 null
        await _ctx.Vm.InitializeAsync();
        var settings = _ctx.Vm.Games[0].LaunchSettings;
        var game = _ctx.Vm.Games[0];

        Assert.Equal("", settings.SelectedResourceQuality!.Tier);
        Assert.False(settings.IsDirty);

        settings.SelectedResourceQuality = settings.ResourceQualities.First(q => q.Tier == "uhd");
        Assert.True(settings.IsDirty);

        await settings.SaveCommand.ExecuteAsync(null);
        Assert.False(settings.Save.Failed);
        Assert.Equal("uhd", game.Game.Launch.ResourceQualityTier);
        Assert.Equal("已更新：启动选项", Assert.Single(_ctx.Vm.Toasts).Message);
        Assert.False(settings.IsDirty);

        settings.SelectedResourceQuality = settings.ResourceQualities.First(q => q.Tier == "");
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Null(game.Game.Launch.ResourceQualityTier);
    }

    [Fact]
    public async Task ResourceQuality_PersistedTier_RestoredOnConstruction()
    {
        // 已保存档位（games.json 手改/上次保存）在设置卡构造时反推选中项——集合内实例
        // （ComboBox 引用匹配），白名单外回默认档。经配置文件注入（RF-3，2026-10-02 二轮
        // review 改造：不依赖「InitializeAsync 不触碰 LaunchSettings 惰性构造」的时序前提——
        // 即使构造发生在初始化早期，tier 也已在配置文件里）
        var configJson = VmFactory.SampleConfigJson.Replace(
            "\"servers\": [ { \"id\": \"cn\", \"name\": \"国服\" } ]",
            "\"launch\": { \"resourceQualityTier\": \"sd\" },\n              \"servers\": [ { \"id\": \"cn\", \"name\": \"国服\" } ]");
        using var ctx = VmFactory.Build(configJson: configJson);
        await ctx.Vm.InitializeAsync();

        var settings = ctx.Vm.Games[0].LaunchSettings;

        Assert.Equal("sd", settings.SelectedResourceQuality!.Tier);
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
    public async Task Save_ToggleOnlyChange_RaisesToastListingLaunchOptions()
    {
        // 2026-09-28 review P2：optionsChanged 曾在 _game.Launch 替换之后才比较（自身比自身恒
        // false），仅开关变更时轻提示完全不弹。必须在替换前对照旧值快照。
        await _ctx.Vm.InitializeAsync();
        var settings = _ctx.Vm.Games[0].LaunchSettings;

        settings.EnableProtonLogDraft = true;
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.False(settings.Save.Failed);

        var toast = Assert.Single(_ctx.Vm.Toasts);
        Assert.Equal("已更新：启动选项", toast.Message);
        Assert.Equal(ToastKind.Success, toast.Kind);
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
    public async Task LanguageSwitch_RepointsLaunchModes_WithoutRewritingDrafts()
    {
        // 回归（2026-09-20 复审）：语言切换重建 LaunchModes 后重指选中项，LaunchModeOption 是
        // 按值相等的 record、重指必然"值不等"，会误触启动方式切换的生成逻辑——
        // 未保存的模板/环境草稿被无声重写。程序化重指必须抑制 changed 副作用
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var settings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"]);
        const string draft = "wine {exe}";
        settings.CommandTemplate = draft;
        settings.EnvironmentText = "MY_UNSAVED_KEY=1";
        var expectedMode = settings.SelectedLaunchMode!.Mode;
        var modesBefore = settings.LaunchModes;

        game.Loc.SetLanguage("en-US");

        var modesAfter = settings.LaunchModes;
        var selAfter = settings.SelectedLaunchMode;

        // 选中项重指到新集合内的实例（引用匹配，否则下拉显示空白），草稿保持原样
        Assert.NotSame(modesBefore, modesAfter);
        Assert.Same(modesAfter.First(m => m.Mode == expectedMode), selAfter);
        Assert.Equal(draft, settings.CommandTemplate);
        Assert.Contains("MY_UNSAVED_KEY=1", settings.EnvironmentText, StringComparison.Ordinal);
        Assert.Equal(draft, settings.CommandTemplate);
        Assert.Contains("MY_UNSAVED_KEY=1", settings.EnvironmentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtonFlavorSwitch_PreservesHalfTypedEnvironmentLine()
    {
        // 回归（2026-09-20）：切发行版即时保存曾把编辑框按"解析→序列化"重写，
        // 用户输入到一半、还没有 "=" 的半行被无声吞掉。
        // 托管语义（2026-09-28）：发行版写入托管字典即时落盘，编辑框文本完全不动
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var settings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"]);
        await settings.SaveCommand.ExecuteAsync(null); // 先落盘构造期推荐配置，切发行版才算"即时保存"

        settings.EnvironmentText = "SAVED_KEY=1\nWINEDLLOVERRIDES";
        settings.SelectedProtonFlavor = "GE-Proton";

        // 半行与既有用户键原样保留；PROTONPATH 不再出现在编辑框文本（托管承载）
        Assert.Contains("WINEDLLOVERRIDES", settings.EnvironmentText, StringComparison.Ordinal);
        Assert.Contains("SAVED_KEY=1", settings.EnvironmentText, StringComparison.Ordinal);
        Assert.DoesNotContain("PROTONPATH", settings.EnvironmentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagedKeys_HiddenFromEditor_ButPersistedOnSave()
    {
        // 托管语义核心：编辑框默认留空（生成/内置键不显示），保存时托管键照常落盘——
        // 存量 wine/Proton 直启模板依赖已保存的 WINEPREFIX/STEAM_COMPAT_DATA_PATH，不能清
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var settings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"]);

        Assert.Equal("", settings.EnvironmentText.Trim());
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.False(settings.Save.Failed);

        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        var environment = reloader.Catalog!.Games[0].Launch.Environment;
        Assert.False(string.IsNullOrWhiteSpace(environment.GetValueOrDefault("GAMEID")));
        Assert.False(string.IsNullOrWhiteSpace(environment.GetValueOrDefault("WINEPREFIX")));
        Assert.False(string.IsNullOrWhiteSpace(environment.GetValueOrDefault("STEAM_COMPAT_DATA_PATH")));
        Assert.Equal("DW-Proton", environment.GetValueOrDefault("PROTONPATH"));
    }

    [Fact]
    public async Task PersistedGeneratedKeys_HiddenFromEditorOnReopen()
    {
        // 变异自查补钉（2026-09-28）：重开设置页时，配置里已持久化的生成键（WINEPREFIX/
        // PROTONPATH 等）同样不进编辑框——显示过滤在构造期，而非只在首运推荐链路径
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        game.Game.Launch.Environment["WINEPREFIX"] = "/tmp/saved-prefix";
        game.Game.Launch.Environment["PROTONPATH"] = "DW-Proton";
        game.Game.Launch.CommandTemplate = "native-umu {exe}"; // 非 Direct，避开首运推荐链改写

        var settings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"]);

        Assert.DoesNotContain("WINEPREFIX", settings.EnvironmentText, StringComparison.Ordinal);
        Assert.DoesNotContain("PROTONPATH", settings.EnvironmentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserTypedKey_OverridesManagedKeyOnSave()
    {
        // 用户在编辑框手输与托管键同名的键：保存时用户键覆盖托管键（显式覆盖能力保留）
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var settings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"]);

        settings.EnvironmentText = "PROTONPATH=My-Custom-Proton";
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.False(settings.Save.Failed);

        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.Equal(
            "My-Custom-Proton",
            reloader.Catalog!.Games[0].Launch.Environment.GetValueOrDefault("PROTONPATH"));
    }

    [Fact]
    public async Task LaunchOptionToggles_PersistOnSave()
    {
        // 启动选项三开关：草稿经"保存启动设置"落盘（与命令模板/环境变量同批）
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var settings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"]);

        settings.UseWaylandDraft = true;
        settings.UpgradeDlssDraft = true;
        settings.EnableProtonLogDraft = true;
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.False(settings.Save.Failed);

        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        var launch = reloader.Catalog!.Games[0].Launch;
        Assert.True(launch.UseWayland);
        Assert.True(launch.UpgradeDlss);
        Assert.True(launch.EnableProtonLog);
    }

    [Fact]
    public async Task LaunchOptionToggle_Change_MarksDirtyThenClearsOnSave()
    {
        // 开关走草稿语义：变化点亮保存钮（IsDirty），保存后复位
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var settings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"]);

        settings.UseWaylandDraft = true;
        Assert.True(settings.IsDirty);

        await settings.SaveCommand.ExecuteAsync(null);
        Assert.False(settings.IsDirty);
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

    [Fact]
    public async Task ProtonFlavorSwitch_DoesNotCarryUnsavedToggleDrafts()
    {
        // 窄通道语义（2026-09-28 review F-A）：发行版即时保存只动 PROTONPATH——
        // 未保存的开关草稿不得被静默带走（toast 也不会提及），脏标须保持点亮
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var settings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"]);

        await settings.SaveCommand.ExecuteAsync(null); // 初始态落盘
        settings.UseWaylandDraft = true; // 草稿变更，未保存
        settings.SelectedProtonFlavor = "GE-Proton"; // 即时保存窄通道

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline
            && !File.ReadAllText(_ctx.ConfigPath).Contains("GE-Proton", StringComparison.Ordinal))
        {
            await Task.Delay(25);
        }

        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        var launch = reloader.Catalog!.Games[0].Launch;
        Assert.Equal("GE-Proton", launch.Environment.GetValueOrDefault("PROTONPATH"));
        Assert.False(launch.UseWayland, "发行版窄通道不得把未保存的开关草稿一并落盘");
        Assert.True(settings.IsDirty, "开关草稿仍未保存，脏标应保持点亮");
    }
}
