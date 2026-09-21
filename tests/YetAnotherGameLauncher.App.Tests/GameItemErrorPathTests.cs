using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// GameItemViewModel 错误/分支矩阵（Phase 4a-续，2026-09-19，审计缺口 GameItemVM 656-717/823-860 等）：
/// 预下载命令成功/失败、启动错误覆盖层的重试链路、并发刷新去重、区域服务器回退。
/// </summary>
[Collection("sequential")]
public class GameItemErrorPathTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeProcessRunner _runner = new();

    public void Dispose() => _temp.Dispose();

    private static byte[] ZipBytes(string inner) => TestZip.Create((inner, "MZ"));

    private void SetupEndfieldPredownload(VmFactory.Context ctx)
    {
        var zip = ZipBytes("bin/ef.exe");
        ctx.Gryphline.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "1.0.0",
            PredownloadAvailable = true,
            PredownloadVersion = "1.1.0",
        };
        ctx.Gryphline.PredownloadManifest = new GameManifest
        {
            Version = "1.1.0",
            EntriesAreArchives = true,
            Files = [new ManifestFile("patch-1.1.0.zip", zip.Length, Hashing.Md5Hex(zip), Url: "https://cdn/patch.zip")],
        };
        ctx.Downloader.Responses["https://cdn/patch.zip"] = zip;
    }

    [Fact]
    public async Task PredownloadCommand_Success_StagesAndReports()
    {
        using var ctx = VmFactory.Build();
        SetupEndfieldPredownload(ctx);
        await ctx.Vm.InitializeAsync();
        var endfield = ctx.Vm.Games[1];

        await endfield.PredownloadCommand.ExecuteAsync(null);

        Assert.True(endfield.HasStagedPredownload);
        Assert.False(endfield.IsBusy);
        Assert.Contains("预下载完成", endfield.StatusText, StringComparison.Ordinal); // 未安装游戏没有 from 版本
        Assert.Contains("1.1.0", endfield.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PredownloadCommand_NoWindow_ReportsFailureWithoutThrow()
    {
        using var ctx = VmFactory.Build();
        await ctx.Vm.InitializeAsync();
        var wuwa = ctx.Vm.Games[0];

        await wuwa.PredownloadCommand.ExecuteAsync(null);

        Assert.False(wuwa.HasStagedPredownload);
        Assert.False(wuwa.IsBusy);
        Assert.NotEqual("", wuwa.StatusText); // 失败文案已上状态行（异常被 catch 转为消息）
    }

    [Fact]
    public async Task ApplyPredownloadCommand_AppliesStagedAndBumpsVersion()
    {
        using var ctx = VmFactory.Build();
        SetupEndfieldPredownload(ctx);
        await ctx.Vm.InitializeAsync();
        var endfield = ctx.Vm.Games[1];
        await endfield.PredownloadCommand.ExecuteAsync(null);

        await endfield.ApplyPredownloadCommand.ExecuteAsync(null);

        Assert.True(endfield.IsInstalled);
        Assert.Equal("1.1.0", new LocalStateService(endfield.InstallDirPath).Load("arknights-endfield", "global")?.Version);
        Assert.False(endfield.HasStagedPredownload); // 应用后暂存清除
    }

    [Fact]
    public async Task LaunchRetry_RetryCommand_ClearsErrorAndRelaunches()
    {
        // 原生 umu 链 + 必然下载失败的准备器：ProtonDownloadFailed → canRetry=true →
        // 覆盖层 RetryCommand 触发 OnLaunchErrorRetryRequested（async void 处理器本体）：
        // 清掉覆盖层、重新启动、再次失败生成新覆盖层
        if (!OperatingSystem.IsLinux())
        {
            // 平台门控（NativeUmuLauncher.EnsureLinux）在非 Linux 上先于准备器抛 Unknown，
            // ProtonDownloadFailed 的重试分类只有 Linux 真实链路可构造；Windows 腿的门控
            // 行为由 NativeUmuLaunchRoutingTests.Windows 分支覆盖
            Assert.Skip("原生 umu 启动链仅 Linux 可达，重试分类由 Linux 腿覆盖");
            return;
        }

        var provisioner = new ThrowingProvisioner();
        var launcher = new NativeUmuLauncher(_runner, provisioner, dataHome: _temp.Path);
        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: [],
            nativeUmu: launcher);
        await ctx.Vm.InitializeAsync();
        var wuwa = ctx.Vm.Games[0];
        Assert.Contains("native-umu", wuwa.Game.Launch.CommandTemplate, StringComparison.Ordinal); // Linux 首运已迁移

        var exePath = Path.Combine(wuwa.InstallDirPath, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        await File.WriteAllTextAsync(exePath, "MZ");
        await wuwa.RefreshAsync();

        await wuwa.LaunchCommand.ExecuteAsync(null);

        Assert.True(wuwa.HasLaunchError);
        Assert.True(wuwa.LaunchError!.CanRetry, "ProtonDownloadFailed 应给出重试动作");
        var firstError = wuwa.LaunchError;

        firstError.RetryCommand.Execute(null);
        await Task.Yield(); // async void 处理器内的 LaunchAsync 需要让出续体

        Assert.True(wuwa.HasLaunchError);
        Assert.NotSame(firstError, wuwa.LaunchError); // 重试后是新一次失败的新覆盖层
    }

    [Fact]
    public async Task LaunchLocalProton_UnknownVersion_GuardKeepsState()
    {
        // OnLaunchErrorLocalProtonSelected 的定位失败守卫：LocateProton 找不到版本 →
        // 直接返回（PROTONPATH 不被写入、覆盖层保持）
        var provisioner = new ThrowingProvisioner();
        var launcher = new NativeUmuLauncher(_runner, provisioner, dataHome: _temp.Path);
        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: [],
            nativeUmu: launcher,
            umuProvisioner: provisioner);
        await ctx.Vm.InitializeAsync();
        var wuwa = ctx.Vm.Games[0];
        var exePath = Path.Combine(wuwa.InstallDirPath, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        await File.WriteAllTextAsync(exePath, "MZ");
        await wuwa.RefreshAsync();
        await wuwa.LaunchCommand.ExecuteAsync(null);
        Assert.True(wuwa.HasLaunchError);
        var before = wuwa.Game.Launch.Environment.GetValueOrDefault("PROTONPATH");

        wuwa.LaunchError!.SelectedLocalProton = "GE-Proton-nonexistent";
        wuwa.LaunchError.UseLocalProtonCommand.Execute(null);
        await Task.Yield();

        Assert.Equal(before, wuwa.Game.Launch.Environment.GetValueOrDefault("PROTONPATH")); // 守卫未写 env
    }

    [Fact]
    public async Task ConcurrentRefresh_SharesVersionCheckTask()
    {
        using var ctx = VmFactory.Build();
        await ctx.Vm.InitializeAsync();
        var requestsAfterInit = ctx.Kuro.VersionInfoRequests.Count;
        var wuwa = ctx.Vm.Games[0];
        wuwa.ResetVersionCheckCache();

        var first = wuwa.RefreshAsync();
        var second = wuwa.RefreshAsync(); // 第一个尚未完成：必须命中同一会话任务
        await first;
        await second;

        Assert.Equal(requestsAfterInit + 1, ctx.Kuro.VersionInfoRequests.Count); // 两次刷新只打一次网络
    }

    [Fact]
    public async Task BackdropRequest_GlobalRegion_SkipsBilibiliServer()
    {
        // SelectServerOptions：region=global 时跳过 B 服（误用 B 服端点的事故防线）
        const string config = """
            {
              "settings": { "installRoot": "~/yagl-test-games", "theme": "Dark" },
              "games": [
                {
                  "id": "wuthering-waves", "displayName": "鸣潮", "channel": "kuro",
                  "installDir": "WutheringWaves", "executable": "Client/game.exe",
                  "servers": [
                    { "id": "bilibili", "name": "B服", "options": { "indexUrl": "https://bili.example/index.json" } },
                    { "id": "cn", "name": "国服", "options": { "indexUrl": "https://cn.example/index.json" } }
                  ]
                }
              ]
            }
            """;
        using var ctx = VmFactory.Build(configJson: config);
        BackdropRequest? captured = null;
        ctx.KuroBackdrop.RequestResolver = r =>
        {
            captured = r;
            return null;
        };

        await ctx.Vm.InitializeAsync();
        ctx.Vm.Loc.SetLanguage("en-US"); // 区域 = global
        await ctx.Vm.Games[0].RefreshAsync();

        Assert.NotNull(captured);
        Assert.Equal("global", captured!.Region);
        // global 无同名服务器：回退取第一个非 B 服（cn）——绝不误用 B 服端点
        Assert.Equal("https://cn.example/index.json", captured.ServerOptions["indexUrl"]);
    }

    /// <summary>必然下载失败的组件准备器（驱动 ProtonDownloadFailed 分类链路）。</summary>
    private sealed class ThrowingProvisioner : IUmuComponentProvisioner
    {
        public bool IsProtonReady(string protonPath) => false;

        public bool IsRuntimeReady(string runtimeVariant) => false;

        public (string Variant, string Name)? ResolveRequiredRuntime(string protonRequest) => null;

        public string? FindInstalledProton(string protonRequest) => null;

        public Task<string> FetchLatestProtonTagAsync(string protonRequest, CancellationToken cancellationToken = default) =>
            throw new LaunchException(LaunchFailureKind.ProtonDownloadFailed, "stub");

        public Task<string> UpdateProtonAsync(
            string protonRequest, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            throw new LaunchException(LaunchFailureKind.ProtonDownloadFailed, "stub");

        public Task<string> EnsureProtonAsync(
            string protonRequest, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            throw new LaunchException(LaunchFailureKind.ProtonDownloadFailed, "组件下载失败（测试桩）");

        public Task EnsureRuntimeAsync(
            string runtimeVariant, string runtimeName, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
