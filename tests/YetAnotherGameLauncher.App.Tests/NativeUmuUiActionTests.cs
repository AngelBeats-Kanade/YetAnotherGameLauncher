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

        public bool IsProtonReady(string protonPath) => ProtonReady;

        public bool IsRuntimeReady(string runtimeVariant) => RuntimeReady;

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
    public async Task LaunchError_UnknownKind_HidesRetry()
    {
        await _ctx.Vm.InitializeAsync();
        var error = new LaunchErrorViewModel(
            _ctx.Vm.Loc,
            "失败",
            failureKind: LaunchFailureKind.Unknown);
        Assert.False(error.CanRetry);
    }
}
