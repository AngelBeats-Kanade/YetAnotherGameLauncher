using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>T6/T7：设置卡组件状态与错误覆盖层重试动作。</summary>
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

        public bool IsProtonReady(string protonPath) => ProtonReady;

        public bool IsRuntimeReady(string runtimeVariant)
        {
            LastRuntimeVariantChecked = runtimeVariant;
            return RuntimeReady;
        }

        public (string Variant, string Name)? ResolveRequiredRuntime(string protonRequest) => ResolvedRuntime;

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
    public async Task LaunchError_DownloadKind_ExposesRetry()
    {
        await _ctx.Vm.InitializeAsync();
        var error = new LaunchErrorViewModel(
            _ctx.Vm.Loc,
            "下载失败",
            failureKind: LaunchFailureKind.ProtonDownloadFailed,
            canRetry: true);

        Assert.True(error.CanRetry);
        Assert.False(error.CanInstallUmu);

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
            _ctx.Vm.Loc,
            "下载失败",
            failureKind: LaunchFailureKind.ProtonDownloadFailed,
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
            _ctx.Vm.Loc,
            "下载失败",
            failureKind: LaunchFailureKind.ProtonDownloadFailed,
            localProtonVersions: []);
        Assert.False(error.CanPickLocalProton);
    }
}
