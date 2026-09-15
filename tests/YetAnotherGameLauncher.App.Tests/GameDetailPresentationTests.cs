using Xunit;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 详情页展示数据测试（方案 A「沉浸影院」）：顶部元信息行（渠道 · 服务器 · 背景来源）
/// 与版本 chip 分段（"本地 x → 最新 y"金色数字富文本的数据面）。纯 VM，无 UI。
/// </summary>
[Collection("sequential")]
public class GameDetailPresentationTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public GameDetailPresentationTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    // ---------- DetailMetaText：标题下元信息行 ----------

    [Fact]
    public async Task DetailMetaText_ComposesChannelAndServer_NoBackdropSegment()
    {
        await _ctx.Vm.InitializeAsync();

        var meta = _ctx.Vm.Games[0].DetailMetaText;
        Assert.StartsWith("Kuro Games", meta);
        Assert.Contains("国服", meta);
        Assert.DoesNotContain("背景", meta); // 无背景素材时来源段整体省略
    }

    [Fact]
    public async Task DetailMetaText_VideoBackdrop_AppendsLoopingSegment()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];
        wuwa.HasBackgroundVideo = true;

        Assert.Contains("背景视频循环播放中", wuwa.DetailMetaText);
        Assert.DoesNotContain("静态背景图", wuwa.DetailMetaText); // 视频优先于静态图
    }

    [Fact]
    public async Task DetailMetaText_ImageBackdrop_AppendsStaticSegment()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];
        wuwa.HasBackgroundImage = true;

        Assert.Contains("静态背景图", wuwa.DetailMetaText);
    }

    [Fact]
    public async Task DetailMetaText_LanguageSwitch_RaisesNotificationAndRelocalizesChip()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];
        Assert.Equal("最新版本", wuwa.VersionChipLead);
        var raised = new List<string>();
        wuwa.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        // 语言切换后刷新：元信息行需重发变更通知（背景来源段为本地化文案），
        // 版本 chip 前导随语言重建（版本检测有会话缓存，值不变但文案必须重算）
        _ctx.Vm.Loc.SetLanguage("en-US");
        await wuwa.RefreshAsync();

        Assert.Contains(nameof(GameItemViewModel.DetailMetaText), raised);
        Assert.Equal("Latest version", wuwa.VersionChipLead);
        Assert.Equal("2.0.0", wuwa.VersionChipNumber);
    }

    // ---------- VersionChip：分段富文本数据 ----------

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
