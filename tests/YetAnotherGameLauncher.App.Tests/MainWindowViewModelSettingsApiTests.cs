using System.Text.Json;
using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// MainWindowViewModel 设置类 API 行为测试（2026-09-22 测试审计补齐：五个生产在用的
/// public 方法——ApplyProxySettingsAsync / ApplySpeedLimitAsync / GetAutostartStateAsync /
/// SetAutostartAsync / StopBackdropVideo——此前在测试中零直接引用，仅部分分支被
/// SettingsViewModel 命令路径间接命中；此处按契约直测成功/失败/清场语义）。
/// </summary>
[Collection("sequential")]
public class MainWindowViewModelSettingsApiTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public MainWindowViewModelSettingsApiTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ApplyProxySettingsAsync_ManualMode_PersistsAddressAndHotAppliesHandler()
    {
        await _ctx.Vm.InitializeAsync();

        var saved = await _ctx.Vm.ApplyProxySettingsAsync(ProxyMode.Manual, "http://127.0.0.1:7890");

        Assert.True(saved);
        Assert.Equal(ProxyMode.Manual, _ctx.CatalogService.Catalog!.Settings.ProxyMode);
        Assert.Equal("http://127.0.0.1:7890", _ctx.CatalogService.Catalog.Settings.ProxyAddress);
        // 落盘断言走模型反序列化（原始文本 Contains 会被转义与子串巧合骗过）
        var persisted = JsonSerializer.Deserialize<GameCatalog>(
            await File.ReadAllTextAsync(_ctx.ConfigPath), Json.Default)!;
        Assert.Equal("http://127.0.0.1:7890", persisted.Settings.ProxyAddress);
        // 热生效：共享 handler 即时切到手动代理（保存即生效契约）
        Assert.True(_ctx.ProxyManager.Handler.UseProxy);
        var proxy = Assert.IsType<System.Net.WebProxy>(_ctx.ProxyManager.Handler.Proxy);
        Assert.Contains("127.0.0.1:7890", proxy.Address?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyProxySettingsAsync_SwitchAwayFromManual_ClearsStaleAddress()
    {
        await _ctx.Vm.InitializeAsync();
        await _ctx.Vm.ApplyProxySettingsAsync(ProxyMode.Manual, "http://127.0.0.1:7890");

        var saved = await _ctx.Vm.ApplyProxySettingsAsync(ProxyMode.System, "http://127.0.0.1:7890");

        Assert.True(saved);
        // 非 Manual 模式地址无意义：残留地址会误导下次切回 Manual 的草稿
        Assert.Null(_ctx.CatalogService.Catalog!.Settings.ProxyAddress);
        var persisted = JsonSerializer.Deserialize<GameCatalog>(
            await File.ReadAllTextAsync(_ctx.ConfigPath), Json.Default)!;
        Assert.Null(persisted.Settings.ProxyAddress);
    }

    [Fact]
    public async Task ApplyProxySettingsAsync_PersistFailure_ReturnsFalse()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("directory permission bits are Unix-only");
        }

        await _ctx.Vm.InitializeAsync();
        MakeUnwritable(_ctx.TempDir.Path);
        try
        {
            var saved = await _ctx.Vm.ApplyProxySettingsAsync(ProxyMode.Manual, "http://127.0.0.1:7890");

            // 契约：返回 false 时调用方不得弹"已保存"（重启后设置回退）
            Assert.False(saved);
        }
        finally
        {
            MakeWritable(_ctx.TempDir.Path);
        }
    }

    [Fact]
    public async Task ApplyProxySettingsAsync_NullCatalog_ReturnsFalse()
    {
        // 配置加载失败（Catalog null）时静默拒绝，不得抛出
        using var broken = VmFactory.Build(configJson: "");
        await broken.Vm.InitializeAsync();
        Assert.True(broken.Vm.ConfigError);

        Assert.False(await broken.Vm.ApplyProxySettingsAsync(ProxyMode.None, ""));
    }

    [Fact]
    public async Task ApplySpeedLimitAsync_AppliesLimiterImmediatelyAndPersists()
    {
        await _ctx.Vm.InitializeAsync();

        var saved = await _ctx.Vm.ApplySpeedLimitAsync(12L * 1024 * 1024);

        Assert.True(saved);
        // 限速必须先于持久化生效（保存失败也不回滚热状态）
        Assert.Equal(12L * 1024 * 1024, _ctx.HttpDownloader.Limiter.BytesPerSecond);
        Assert.Equal(12L * 1024 * 1024, _ctx.CatalogService.Catalog!.Settings.DownloadSpeedLimitBytes);
        var persisted = JsonSerializer.Deserialize<GameCatalog>(
            await File.ReadAllTextAsync(_ctx.ConfigPath), Json.Default)!;
        Assert.Equal(12L * 1024 * 1024, persisted.Settings.DownloadSpeedLimitBytes);
    }

    [Fact]
    public async Task ApplySpeedLimitAsync_NullCatalog_LimiterStillApplied_ReturnsFalse()
    {
        using var broken = VmFactory.Build(configJson: "");
        await broken.Vm.InitializeAsync();

        var saved = await broken.Vm.ApplySpeedLimitAsync(1024);

        // 目录不可持久化，但共享下载器的热限速已生效（与代理"先应用再保存"同序）
        Assert.False(saved);
        Assert.Equal(1024, broken.HttpDownloader.Limiter.BytesPerSecond);
    }

    [Fact]
    public async Task Autostart_GetDelegates_SetSucceeds_ReturnsTrueWithoutErrorFlag()
    {
        var autostart = new FakeAutostartService { Enabled = false };
        using var ctx = VmFactory.Build(autostart: autostart);
        await ctx.Vm.InitializeAsync();

        Assert.False(await ctx.Vm.GetAutostartStateAsync());
        Assert.True(await ctx.Vm.SetAutostartAsync(true));

        Assert.Equal([true], autostart.SetCalls);
        Assert.True(autostart.Enabled);
        Assert.False(ctx.Vm.ConfigError); // 成功路径不得误报错误
    }

    [Fact]
    public async Task SetAutostartAsync_ServiceThrows_ReturnsFalseFlagsErrorWithReason()
    {
        var autostart = new FakeAutostartService { SetEnabledFailure = new IOException("注册表写入被拒绝") };
        using var ctx = VmFactory.Build(autostart: autostart);
        await ctx.Vm.InitializeAsync();

        var result = await ctx.Vm.SetAutostartAsync(true);

        Assert.False(result);
        Assert.True(ctx.Vm.ConfigError);
        Assert.Contains("注册表写入被拒绝", ctx.Vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAutostartAsync_StateMismatchAfterSet_ReturnsFalseSilently()
    {
        // 写入"成功"但状态没变（如策略拦截了自启项）：返回 false，但不置全局错误
        var autostart = new FakeAutostartService { Enabled = false, ApplyEnabledOnSet = false };
        using var ctx = VmFactory.Build(autostart: autostart);
        await ctx.Vm.InitializeAsync();

        Assert.False(await ctx.Vm.SetAutostartAsync(true));

        Assert.Equal([true], autostart.SetCalls);
        Assert.False(ctx.Vm.ConfigError);
    }

    [Fact]
    public async Task StopBackdropVideo_StopsEveryGamePlayer_ReentryRestartsFreshSession()
    {
        var tempDir = new TempDir();
        try
        {
            using var ctx = VmFactory.Build();
            var video = tempDir.FilePath("cached", "backdrop.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(video)!);
            await File.WriteAllTextAsync(video, "fake");
            ctx.KuroBackdrop.Resolver = _ => new BackdropSource(video, BackdropKind.Video);
            ctx.GryphlineBackdrop.Resolver = _ => new BackdropSource(video, BackdropKind.Video);
            await ctx.Vm.InitializeAsync();
            Assert.Equal(2, ctx.Players.Count);

            // 停在游戏 0（起播已完成），切设置页暂停保活（会话仍活）
            await ctx.Vm.ShowSettingsCommand.ExecuteAsync(null);
            Assert.Equal(1, ctx.Players[0].PauseCount);

            ctx.Vm.StopBackdropVideo();

            // 契约：退出前逐游戏全停——含从未起播的游戏（其播放器也须 Stop 清帧缓冲）
            Assert.False(ctx.Players[0].IsSessionActive);
            Assert.False(ctx.Players[1].IsSessionActive);
            var playedBefore = ctx.Players[0].PlayedPaths.Count;

            // 重进详情页：会话已死 → 全新起播（而非 Resume 复用已停会话）
            ctx.Vm.GameNavSelection = ctx.Vm.Games[0];
            await Task.Delay(50); // VideoStartDeferral 已由 VmFactory 置零，冲刷后台起播
            Assert.Equal(playedBefore + 1, ctx.Players[0].PlayedPaths.Count);
            Assert.Equal(0, ctx.Players[0].ResumeCount);
        }
        finally
        {
            tempDir.Dispose();
        }
    }

    /// <summary>目录权限位注入写失败（与 SettingsSaveFailureTests 同款；DAC 豁免进程下跳过）。</summary>
    private static void MakeUnwritable(string dir)
    {
        if (!DacExemptionProbe.TryMakeDirectoryUnwritable(dir))
        {
            Assert.Skip("探针检出读权限检查被豁免（root/CAP_DAC_OVERRIDE 等能力豁免），拒访写失败形态不可保证构造");
        }
    }

    private static void MakeWritable(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
