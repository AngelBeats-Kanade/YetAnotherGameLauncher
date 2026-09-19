using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// LaunchSettingsViewModel 矩阵（Phase 4a-续，2026-09-19，审计缺口 LaunchSettingsVM 212-260/322-404/453-514）：
/// 启动方式切换的模板/环境变量生成与清理、组件状态文本三态、准备/检查更新的失败分支。
/// </summary>
[Collection("sequential")]
public class LaunchSettingsMatrixTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public LaunchSettingsMatrixTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.Dispose();

    private LaunchSettingsViewModel NewSettings(
        FakePlatformInfo? platform = null,
        IUmuComponentProvisioner? provisioner = null,
        IReadOnlyList<string>? protonVersions = null) =>
        new(
            _ctx.Vm.Games[0].Game, _ctx.Vm.Games[0].InstallDirPath, _ctx.CatalogService,
            _ctx.Vm.Games[0].Loc, _ctx.Vm.Games[0],
            platformInfo: platform ?? new FakePlatformInfo(isLinux: true),
            protonVersions: protonVersions ?? ["GE-Proton10-9"],
            umuProvisioner: provisioner);

    [Fact]
    public async Task LaunchModeSwitch_Direct_ClearsGeneratedEnv()
    {
        await _ctx.Vm.InitializeAsync();
        var settings = NewSettings();
        Assert.Contains("PROTONPATH", settings.EnvironmentText, StringComparison.Ordinal); // 前置：umu 态有生成 env

        settings.SelectedLaunchMode = settings.LaunchModes.First(m => m.Mode == LaunchMode.Direct);

        Assert.Equal("{exe}", settings.CommandTemplate);
        Assert.DoesNotContain("PROTONPATH", settings.EnvironmentText, StringComparison.Ordinal);
        Assert.DoesNotContain("GAMEID", settings.EnvironmentText, StringComparison.Ordinal);
        Assert.DoesNotContain("STEAM_COMPAT", settings.EnvironmentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchModeSwitch_DirectThenUmu_RegeneratesEnvAndKeepsCustomVars()
    {
        await _ctx.Vm.InitializeAsync();
        var settings = NewSettings();
        settings.EnvironmentText += "\nMY_CUSTOM_VAR=keep-me";

        settings.SelectedLaunchMode = settings.LaunchModes.First(m => m.Mode == LaunchMode.Direct);
        settings.SelectedLaunchMode = settings.LaunchModes.First(m => m.Mode == LaunchMode.NativeUmu);

        Assert.Contains("native-umu {exe}", settings.CommandTemplate, StringComparison.Ordinal);
        Assert.Contains("MY_CUSTOM_VAR=keep-me", settings.EnvironmentText, StringComparison.Ordinal); // 用户变量保留
        Assert.Contains("GAMEID=", settings.EnvironmentText, StringComparison.Ordinal); // 生成 env 重新合并
    }

    [Fact]
    public async Task NativeUmuStatus_ReflectsProtonAndRuntimeReadiness()
    {
        await _ctx.Vm.InitializeAsync();
        var provisioner = new ScriptedProvisioner();
        var settings = NewSettings(provisioner: provisioner);

        // 全缺 → components_missing（状态仅在发行版变化时重算：用三个不同发行版驱动三态）
        provisioner.ProtonReady = false;
        provisioner.RuntimeReady = false;
        settings.SelectedProtonFlavor = "DW-Proton";
        Assert.Equal(_ctx.Vm.Loc["launch_native_components_missing"], settings.NativeUmuStatusText);

        // 仅 Proton 就绪 → runtime_missing
        provisioner.ProtonReady = true;
        settings.SelectedProtonFlavor = "GE-Proton";
        Assert.Equal(_ctx.Vm.Loc["launch_native_runtime_missing"], settings.NativeUmuStatusText);

        // 全就绪 → ready
        provisioner.RuntimeReady = true;
        settings.SelectedProtonFlavor = "UMU-Proton";
        Assert.Equal(_ctx.Vm.Loc["launch_native_components_ready"], settings.NativeUmuStatusText);
    }

    [Fact]
    public async Task PrepareUmuComponents_Failure_ShowsFailureInSaveSlot()
    {
        await _ctx.Vm.InitializeAsync();
        var provisioner = new ScriptedProvisioner
        {
            EnsureProtonError = "下载被防火墙拦截（测试桩）",
        };
        var settings = NewSettings(provisioner: provisioner);

        await settings.PrepareUmuComponentsCommand.ExecuteAsync(null);

        Assert.True(settings.Save.Failed);
        Assert.Contains("防火墙", settings.Save.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckProtonUpdate_FetchFailure_ResetsStateWithoutCrash()
    {
        await _ctx.Vm.InitializeAsync();
        var provisioner = new ScriptedProvisioner
        {
            FetchTagError = "上游不可达（测试桩）",
        };
        var settings = NewSettings(provisioner: provisioner);
        settings.SelectedProtonFlavor = "GE-Proton";

        await settings.CheckProtonUpdateCommand.ExecuteAsync(null);

        Assert.False(settings.ShowProtonUpdateConfirm);
        Assert.NotEqual(ProtonUpdateCheckState.Checking, settings.ProtonUpdateState); // 不卡在 Checking
    }

    [Fact]
    public async Task CheckProtonUpdate_WhileChecking_SecondCallIgnoredUntilSettled()
    {
        // 审计缺口重入守卫（2026-09-19）：Checking 期间再点检查按钮必须立即返回（不并发第二次查询）；
        // 查询落地后才进入 UpdateAvailable 并弹确认覆盖层
        await _ctx.Vm.InitializeAsync();
        var provisioner = new GatedProvisioner();
        var settings = NewSettings(provisioner: provisioner);
        settings.SelectedProtonFlavor = "GE-Proton";

        var first = settings.CheckProtonUpdateCommand.ExecuteAsync(null);
        Assert.Equal(ProtonUpdateCheckState.Checking, settings.ProtonUpdateState); // 第一次调用同步进入 Checking
        await settings.CheckProtonUpdateCommand.ExecuteAsync(null); // 重入：守卫直接返回

        Assert.Equal(1, provisioner.FetchCalls);

        provisioner.Gate.SetResult("GE-Proton11-7");
        await first;

        Assert.Equal(ProtonUpdateCheckState.UpdateAvailable, settings.ProtonUpdateState);
        Assert.True(settings.ShowProtonUpdateConfirm);
        Assert.Equal("GE-Proton11-7", settings.PendingProtonUpdateTag);
    }

    /// <summary>查询门栓准备器：FetchLatestProtonTagAsync 挂起直到测试放行（重入窗口可控）。</summary>
    private sealed class GatedProvisioner : IUmuComponentProvisioner
    {
        public TaskCompletionSource<string> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int FetchCalls { get; private set; }

        public bool IsProtonReady(string protonPath) => false;

        public bool IsRuntimeReady(string runtimeVariant) => false;

        public (string Variant, string Name)? ResolveRequiredRuntime(string protonRequest) => null;

        public string? FindInstalledProton(string protonRequest) => null;

        public Task<string> FetchLatestProtonTagAsync(string protonRequest, CancellationToken cancellationToken = default)
        {
            FetchCalls++;
            return Gate.Task;
        }

        public Task<string> UpdateProtonAsync(
            string protonRequest, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult("/tmp/GE-Proton");

        public Task<string> EnsureProtonAsync(
            string protonRequest, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult("/tmp/GE-Proton");

        public Task EnsureRuntimeAsync(
            string runtimeVariant, string runtimeName, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>可编程组件准备器：正/异常路径均可注入（状态文本与失败分支矩阵）。</summary>
    private sealed class ScriptedProvisioner : IUmuComponentProvisioner
    {
        public bool ProtonReady { get; set; }
        public bool RuntimeReady { get; set; }
        public string? EnsureProtonError { get; set; }
        public string? FetchTagError { get; set; }

        public bool IsProtonReady(string protonPath) => ProtonReady;

        public bool IsRuntimeReady(string runtimeVariant) => RuntimeReady;

        public (string Variant, string Name)? ResolveRequiredRuntime(string protonRequest) => null;

        public string? FindInstalledProton(string protonRequest) => ProtonReady ? "/tmp/GE-Proton" : null;

        public Task<string> FetchLatestProtonTagAsync(string protonRequest, CancellationToken cancellationToken = default)
        {
            if (FetchTagError is not null)
            {
                throw new LaunchException(LaunchFailureKind.ProtonDownloadFailed, FetchTagError);
            }

            return Task.FromResult("GE-Proton11-7");
        }

        public Task<string> UpdateProtonAsync(
            string protonRequest, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult("/tmp/GE-Proton");

        public Task<string> EnsureProtonAsync(
            string protonRequest, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            if (EnsureProtonError is not null)
            {
                throw new LaunchException(LaunchFailureKind.ProtonDownloadFailed, EnsureProtonError);
            }

            ProtonReady = true;
            return Task.FromResult("/tmp/GE-Proton");
        }

        public Task EnsureRuntimeAsync(
            string runtimeVariant, string runtimeName, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            RuntimeReady = true;
            return Task.CompletedTask;
        }
    }
}
