using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>T6/T7：设置卡组件状态与错误覆盖层重试动作。</summary>
[Collection("sequential")]
public sealed class NativeUmuUiActionTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public NativeUmuUiActionTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.Dispose();

    private sealed class FakeProvisioner : IUmuComponentProvisioner
    {
        public bool ProtonReady { get; set; }
        public bool RuntimeReady { get; set; }
        public int EnsureProtonCalls { get; private set; }
        public int EnsureRuntimeCalls { get; private set; }

        /// <summary>ResolveRequiredRuntime 的固定返回（null = 走 VM 的默认 Runtime 回退）。</summary>
        public (string Variant, string Name)? ResolvedRuntime { get; set; }

        /// <summary>最近一次 IsRuntimeReady 询问的 variant（验证状态卡按 manifest 解析结果询问）。</summary>
        public string? LastRuntimeVariantChecked { get; private set; }

        /// <summary>EnsureRuntimeAsync 收到的 (variant, name)（验证一键下载按 manifest 结果准备）。</summary>
        public (string Variant, string Name)? RuntimePrepared { get; private set; }

        /// <summary>FindInstalledProton 的固定返回（null = 未安装）。</summary>
        public string? InstalledProtonPath { get; set; }

        /// <summary>FetchLatestProtonTagAsync 的固定返回。</summary>
        public string LatestTag { get; set; } = "GE-Proton11-6";

        /// <summary>FetchLatestProtonTagAsync 收到的请求（验证按所选发行版查询）。</summary>
        public string? LastTagRequest { get; private set; }

        public int FetchTagCalls { get; private set; }

        public int UpdateCalls { get; private set; }

        /// <summary>UpdateProtonAsync 收到的请求。</summary>
        public string? LastUpdateRequest { get; private set; }

        /// <summary>UpdateProtonAsync 的返回路径。</summary>
        public string UpdateResultPath { get; set; } = "/compat/GE-Proton11-7";

        public bool IsProtonReady(string protonPath) => ProtonReady;

        public bool IsRuntimeReady(string runtimeVariant)
        {
            LastRuntimeVariantChecked = runtimeVariant;
            return RuntimeReady;
        }

        public (string Variant, string Name)? ResolveRequiredRuntime(string protonRequest) => ResolvedRuntime;

        // ProtonReady 旗标 = "已装在默认路径"；Ensure/Update 后 FindInstalledProton 必须能找到
        public string? FindInstalledProton(string protonRequest) =>
            InstalledProtonPath ?? (ProtonReady ? "/tmp/GE-Proton" : null);

        public Task<string> FetchLatestProtonTagAsync(string protonRequest, CancellationToken cancellationToken = default)
        {
            FetchTagCalls++;
            LastTagRequest = protonRequest;
            return Task.FromResult(LatestTag);
        }

        public Task<string> UpdateProtonAsync(
            string protonRequest, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            LastUpdateRequest = protonRequest;
            ProtonReady = true;
            InstalledProtonPath = UpdateResultPath; // 更新后本地可解析到新版
            return Task.FromResult(UpdateResultPath);
        }

        public Task<string> EnsureProtonAsync(
            string protonRequest, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            EnsureProtonCalls++;
            ProtonReady = true;
            return Task.FromResult("/tmp/GE-Proton");
        }

        public Task EnsureRuntimeAsync(
            string runtimeVariant, string runtimeName,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            EnsureRuntimeCalls++;
            RuntimePrepared = (runtimeVariant, runtimeName);
            RuntimeReady = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task LaunchSettings_NativeMode_ShowsMissingStatusAndPreparesComponents()
    {
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var provisioner = new FakeProvisioner();
        var platform = new FakePlatformInfo(isLinux: true);

        var settings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: platform, umuProvisioner: provisioner);

        Assert.True(settings.IsNativeUmuMode);
        Assert.Contains("Proton", settings.NativeUmuStatusText, StringComparison.Ordinal);
        Assert.False(provisioner.ProtonReady);

        await settings.PrepareUmuComponentsCommand.ExecuteAsync(null);

        Assert.Equal(1, provisioner.EnsureProtonCalls);
        Assert.Equal(1, provisioner.EnsureRuntimeCalls);
        Assert.True(provisioner.ProtonReady);
        Assert.True(provisioner.RuntimeReady);
        Assert.Contains("就绪", settings.NativeUmuStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchSettings_RuntimeResolvedFromManifest_DrivesStatusAndPrepare()
    {
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        // 老版 Proton（GE-Proton9 系）声明 sniper/steamrt3：状态询问与一键下载都必须用它，
        // 而不是 SteamRuntimeCatalog.Default 的 steamrt4
        var provisioner = new FakeProvisioner
        {
            ProtonReady = true,
            RuntimeReady = true,
            ResolvedRuntime = ("steamrt3", "sniper"),
        };
        var platform = new FakePlatformInfo(isLinux: true);

        var settings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: platform, umuProvisioner: provisioner);

        Assert.Equal("steamrt3", provisioner.LastRuntimeVariantChecked);
        Assert.Contains("就绪", settings.NativeUmuStatusText, StringComparison.Ordinal);

        await settings.PrepareUmuComponentsCommand.ExecuteAsync(null);

        Assert.Equal(1, provisioner.EnsureRuntimeCalls);
        Assert.Equal(("steamrt3", "sniper"), provisioner.RuntimePrepared);
    }

    [Fact]
    public async Task LaunchSettings_CheckProtonUpdate_NewVersion_ShowsConfirmAndButtonBecomesUpdate()
    {
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var provisioner = new FakeProvisioner
        {
            InstalledProtonPath = "/compat/GE-Proton11-6",
            LatestTag = "GE-Proton11-7",
        };

        var settings = NewLinuxNativeUmuSettings(game.Game, game.InstallDirPath, provisioner);
        settings.SelectedProtonFlavor = "GE-Proton";

        await settings.CheckProtonUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, provisioner.FetchTagCalls);
        Assert.Equal("GE-Proton", provisioner.LastTagRequest);
        Assert.True(settings.ShowProtonUpdateConfirm);
        Assert.Equal(ProtonUpdateCheckState.UpdateAvailable, settings.ProtonUpdateState);
        Assert.Contains("GE-Proton11-7", settings.ProtonCheckButtonText, StringComparison.Ordinal);
        // 版本号过连字符插入了 WORD JOINER（U+2060）防拆行，断言按同构规则构造期望值
        Assert.Contains("GE-Proton11-6".Replace("-", "-\u2060"), settings.ProtonUpdateConfirmMessage, StringComparison.Ordinal);
        Assert.Contains("正在运行", settings.ProtonUpdateConfirmMessage, StringComparison.Ordinal); // 删旧版前的运行中提示

        // 取消只关覆盖层：按钮保持"更新"态
        settings.CancelProtonUpdateCommand.Execute(null);
        Assert.False(settings.ShowProtonUpdateConfirm);
        Assert.Equal(ProtonUpdateCheckState.UpdateAvailable, settings.ProtonUpdateState);
        Assert.Contains("GE-Proton11-7", settings.ProtonCheckButtonText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchSettings_ConfirmProtonUpdate_UpdatesAndResetsButton()
    {
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var provisioner = new FakeProvisioner
        {
            InstalledProtonPath = "/compat/GE-Proton11-6",
            LatestTag = "GE-Proton11-7",
        };

        var settings = NewLinuxNativeUmuSettings(game.Game, game.InstallDirPath, provisioner);
        settings.SelectedProtonFlavor = "GE-Proton";
        await settings.CheckProtonUpdateCommand.ExecuteAsync(null);

        await settings.ConfirmProtonUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, provisioner.UpdateCalls);
        Assert.Equal("GE-Proton", provisioner.LastUpdateRequest);
        Assert.False(settings.ShowProtonUpdateConfirm);
        Assert.Equal(ProtonUpdateCheckState.Idle, settings.ProtonUpdateState);
        Assert.Null(settings.PendingProtonUpdateTag);
        Assert.Contains("GE-Proton11-7", settings.Save.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchSettings_CheckProtonUpdate_UpToDate_NoDialog()
    {
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var provisioner = new FakeProvisioner
        {
            InstalledProtonPath = "/compat/GE-Proton11-6",
            LatestTag = "GE-Proton11-6",
        };

        var settings = NewLinuxNativeUmuSettings(game.Game, game.InstallDirPath, provisioner);

        await settings.CheckProtonUpdateCommand.ExecuteAsync(null);

        Assert.False(settings.ShowProtonUpdateConfirm);
        Assert.Equal(ProtonUpdateCheckState.Idle, settings.ProtonUpdateState);
        Assert.Contains("GE-Proton11-6", settings.Save.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchSettings_FlavorSelection_PersistsProtonPathImmediately()
    {
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var provisioner = new FakeProvisioner { ProtonReady = true, RuntimeReady = true };

        var settings = NewLinuxNativeUmuSettings(game.Game, game.InstallDirPath, provisioner);
        settings.SelectedProtonFlavor = "GE-Proton";

        // 选择即落盘：草稿与模型同步更新（启动链读已保存 env，不再有草稿/存盘错位）
        Assert.Equal("GE-Proton", game.Game.Launch.Environment.GetValueOrDefault("PROTONPATH"));
        Assert.Contains("PROTONPATH=GE-Proton", settings.EnvironmentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchSettings_LanguageSwitch_RefreshesCheckButtonText()
    {
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        var provisioner = new FakeProvisioner { ProtonReady = true, RuntimeReady = true };

        var settings = NewLinuxNativeUmuSettings(game.Game, game.InstallDirPath, provisioner);

        // 计算属性文案不在 Loc[key] 绑定路径上，靠 VM 订阅 Item[] 通知手动刷新
        game.Loc.SetLanguage("en-US");
        Assert.Equal("Check for updates", settings.ProtonCheckButtonText);
        game.Loc.SetLanguage("zh-CN");
        Assert.Equal("检查更新", settings.ProtonCheckButtonText);
    }

    private LaunchSettingsViewModel NewLinuxNativeUmuSettings(
        Core.Models.GameDefinition definition, string installDir, FakeProvisioner provisioner) =>
        new(
            definition, installDir, _ctx.CatalogService, _ctx.Vm.Games[0].Loc, _ctx.Vm.Games[0],
            platformInfo: new FakePlatformInfo(isLinux: true), umuProvisioner: provisioner);

    [Fact]
    public async Task LaunchError_DownloadKind_ExposesRetry()
    {
        await _ctx.Vm.InitializeAsync();
        var error = new LaunchErrorViewModel(
            "下载失败",
            canRetry: true);

        Assert.True(error.CanRetry);

        var retried = false;
        error.RetryRequested += (_, _) => retried = true;
        error.RetryCommand.Execute(null);
        Assert.True(retried);
    }

    [Fact]
    public async Task LaunchError_ProtonDownload_ExposesLocalProtonPicker()
    {
        await _ctx.Vm.InitializeAsync();
        string? picked = null;
        var error = new LaunchErrorViewModel(
            "下载失败",
            canRetry: true,
            localProtonVersions: ["GE-Proton10-9", "dw-proton"]);
        error.LocalProtonSelected += (_, v) => picked = v;
        error.SelectedLocalProton = "dw-proton";
        error.UseLocalProtonCommand.Execute(null);

        Assert.True(error.CanPickLocalProton);
        Assert.Equal("dw-proton", picked);
    }

    [Fact]
    public async Task LaunchError_NoLocalProtons_HidesPicker()
    {
        await _ctx.Vm.InitializeAsync();
        var error = new LaunchErrorViewModel(
            "下载失败",
            localProtonVersions: []);
        Assert.False(error.CanPickLocalProton);
    }
}
