using Xunit;

using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// native umu（默认推荐启动模式）路由端到端：启动设置卡保存的参数（native-umu 模板、
/// 发行版代号 PROTONPATH、自定义环境变量、umuId）→ GameItemViewModel.LaunchAsync 路由 →
/// NativeUmuLauncher 组装 → FakeProcessRunner 收到的最终容器命令与环境逐项断言。
/// Linux 专用（原生 umu 仅 Linux）。串行集合：与 headless 会话用户同队，避免并行调度踩中平台初始化竞态。
/// </summary>
[Collection("sequential")]
public class NativeUmuLaunchRoutingTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeProcessRunner _runner = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task SavedNativeUmuSettings_LandInFinalContainerCommandAndEnv()
    {
        if (!OperatingSystem.IsLinux())
        {
            // 审计修复（2026-09-19）：原实现直接 return（Windows CI 零断言静默绿）。
            // Windows 腿反向断言平台门控本身：原生 umu 链在非 Linux 上不可用，
            // 启动必须给出明确失败态，且绝不产出 umu 容器命令。
            // 注意必须用 isLinux:true 假平台驱动首运迁移，模板才会变成 native-umu 并路由进
            // NativeUmuLauncher 命中 EnsureLinux 门控；默认 isLinux:false 时模板保持直连模式，
            // 启动经 FakeProcessRunner 成功、门控根本不可达（2026-09-21 Windows CI 实锤）。
            using var winCtx = VmFactory.Build(
                configJson: null,
                templateFactory: () => VmFactory.SampleConfigJson,
                platformInfo: new FakePlatformInfo(isLinux: true),
                linuxProtonVersions: [],
                nativeUmu: new NativeUmuLauncher(_runner, provisioner: null, dataHome: _temp.Path));
            await winCtx.Vm.InitializeAsync();
            var winGame = winCtx.Vm.Games[0];

            var exePath = Path.Combine(
                winGame.InstallDirPath, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            await File.WriteAllTextAsync(exePath, "MZ");
            await winGame.RefreshAsync();

            await winGame.LaunchAsync();

            Assert.True(winGame.HasLaunchError, "Windows 上原生 umu 链不可用：启动应给出失败态");
            Assert.All(_runner.Specs, s =>
                Assert.False(s.FileName.Replace('\\', '/').Contains("_v2-entry-point", StringComparison.Ordinal),
                    "Windows 上不得产出 umu 容器命令"));
            return;
        }

        var protonDir = CreateFakeProton();
        var runtimeDir = UmuPaths.RuntimeDirectory("steamrt4", _temp.Path);
        Directory.CreateDirectory(runtimeDir);
        File.WriteAllText(Path.Combine(runtimeDir, "_v2-entry-point"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(runtimeDir, UmuPaths.InstallMarkerName), "ok");
        var provisioner = new FakeProvisioner(protonDir);

        var launcher = new NativeUmuLauncher(_runner, provisioner, dataHome: _temp.Path);
        using var ctx = VmFactory.Build(nativeUmu: launcher);
        await ctx.Vm.InitializeAsync();
        var game = ctx.Vm.Games[0];

        // 安装目录 + 可执行文件真实存在
        var installDir = _temp.FilePath("game");
        Directory.CreateDirectory(installDir);
        await File.WriteAllTextAsync(Path.Combine(installDir, "Game.exe"), "x");

        // 启动设置卡：native umu 模式 + 发行版代号 + 自定义环境变量 + umuId
        var launchSettings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true),
            protonVersions: ["GE-Proton10-9"],
            umuProvisioner: provisioner);
        launchSettings.InstallDirDraft = installDir;
        launchSettings.ExecutableDraft = "Game.exe";
        launchSettings.EnvironmentText = "PROTONPATH=DW-Proton\r\nCUSTOM_FLAG=hello";
        game.Game.Launch.UmuId = "umu-3513350";
        await launchSettings.SaveCommand.ExecuteAsync(null);
        Assert.False(launchSettings.Save.Failed);

        // 启动：路由到 NativeUmuLauncher 并落到 FakeProcessRunner
        await game.LaunchAsync();
        var spec = Assert.Single(_runner.Specs);

        // 容器入口：Steam Runtime 的 _v2-entry-point + proton 脚本
        Assert.Contains("_v2-entry-point", spec.FileName.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains("proton", spec.Arguments, StringComparison.Ordinal);
        Assert.Contains("waitforexitandrun", spec.Arguments, StringComparison.Ordinal);

        // 环境逐项：prefix 定位、umuId 覆盖、代号解析为绝对路径、用户自定义变量透传
        var env = spec.Environment!;
        Assert.Equal(Normalize(CompatTools.PrefixPathFor("wuthering-waves", _temp.Path)),
            Normalize(env["WINEPREFIX"]));
        Assert.Equal("umu-3513350", env["UMU_ID"]);
        Assert.Equal("umu-3513350", env["GAMEID"]);
        Assert.Equal(Normalize(protonDir), Normalize(env["PROTONPATH"]));
        Assert.Equal("hello", env["CUSTOM_FLAG"]);
        // 配置里的代号不得泄漏到最终环境（已解析为绝对路径）
        Assert.NotEqual("DW-Proton", env["PROTONPATH"]);
    }

    /// <summary>可编程组件准备器：EnsureProtonAsync 返回预先落盘的假 Proton 目录。</summary>
    private sealed class FakeProvisioner(string protonDir) : IUmuComponentProvisioner
    {
        public bool IsProtonReady(string protonPath) => false;

        public bool IsRuntimeReady(string runtimeVariant) => true;

        public (string Variant, string Name)? ResolveRequiredRuntime(string protonRequest) => null;

        public Task<string> EnsureProtonAsync(
            string protonRequest, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(protonDir);

        public Task EnsureRuntimeAsync(
            string runtimeVariant, string runtimeName,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public string? FindInstalledProton(string protonRequest) => protonDir;

        public Task<string> FetchLatestProtonTagAsync(string protonRequest, CancellationToken cancellationToken = default)
            => Task.FromResult(protonRequest);

        public Task<string> UpdateProtonAsync(
            string protonRequest, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(protonDir);
    }

    private string CreateFakeProton()
    {
        var dir = _temp.FilePath("GE-Proton");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "toolmanifest.vdf"), """
            "manifest"
            {
              "commandline" "/proton %verb%"
              "compatmanager_layer_name" "proton"
              "require_tool_appid" "4183110"
            }
            """);
        File.WriteAllText(Path.Combine(dir, "proton"), "#!/bin/sh\n");
        return dir;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
