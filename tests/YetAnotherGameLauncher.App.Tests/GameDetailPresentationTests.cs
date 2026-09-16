using Xunit;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 详情页展示数据测试（方案 A「沉浸影院」）：版本 chip 分段
/// （"本地 x → 最新 y"金色数字富文本的数据面）。纯 VM，无 UI。
/// </summary>
[Collection("sequential")]
public class GameDetailPresentationTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public GameDetailPresentationTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    // ---------- VersionChip：分段富文本数据 ----------

    [Fact]
    public async Task VersionChip_LanguageSwitch_RelocalizesSegments()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];
        Assert.Equal("最新版本", wuwa.VersionChipLead);

        // 语言切换后刷新：版本 chip 前导随语言重建（版本检测有会话缓存，值不变但文案必须重算）
        _ctx.Vm.Loc.SetLanguage("en-US");
        await wuwa.RefreshAsync();

        Assert.Equal("Latest version", wuwa.VersionChipLead);
        Assert.Equal("2.0.0", wuwa.VersionChipNumber);
    }

    [Fact]
    public async Task VersionChip_NotInstalled_ShowsLatestVersionOnly()
    {
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.8.0" };
        await _ctx.Vm.InitializeAsync();

        var wuwa = _ctx.Vm.Games[0];
        Assert.Equal("最新版本", wuwa.VersionChipLead);
        Assert.Equal("3.8.0", wuwa.VersionChipNumber);
        Assert.Equal("", wuwa.VersionChipMid);
        Assert.Equal("", wuwa.VersionChipTarget);
    }

    [Fact]
    public async Task VersionChip_InstalledUpToDate_ShowsLocalVersionOnly()
    {
        var installDir = Path.Combine(_ctx.TempDir.FilePath("games-root"), "WutheringWaves");
        await new LocalStateService(installDir).SaveAsync(new LocalGameState
        {
            GameId = "wuthering-waves",
            ServerId = "cn",
            Version = "3.6.0",
        });
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.6.0" };

        await _ctx.Vm.InitializeAsync();

        var wuwa = _ctx.Vm.Games[0];
        Assert.Equal("本地", wuwa.VersionChipLead);
        Assert.Equal("3.6.0", wuwa.VersionChipNumber);
        Assert.Equal("", wuwa.VersionChipMid);
        Assert.Equal("", wuwa.VersionChipTarget);
    }

    [Fact]
    public async Task VersionChip_HasUpdate_ShowsMigrationPair()
    {
        // 预写本地状态 3.6.0 + 远端 3.8.0：初始化的首次版本检测即看到迁移对
        // （RefreshAsync 的版本检测按服务器有会话缓存，初始化后再改 VersionInfo 不会被看到）
        var installDir = Path.Combine(_ctx.TempDir.FilePath("games-root"), "WutheringWaves");
        await new LocalStateService(installDir).SaveAsync(new LocalGameState
        {
            GameId = "wuthering-waves",
            ServerId = "cn",
            Version = "3.6.0",
        });
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.8.0" };

        await _ctx.Vm.InitializeAsync();

        var wuwa = _ctx.Vm.Games[0];
        Assert.Equal("本地", wuwa.VersionChipLead);
        Assert.Equal("3.6.0", wuwa.VersionChipNumber);
        Assert.Equal("→ 最新", wuwa.VersionChipMid);
        Assert.Equal("3.8.0", wuwa.VersionChipTarget);
        Assert.Equal("有可用更新", wuwa.StatusText);
    }
}
