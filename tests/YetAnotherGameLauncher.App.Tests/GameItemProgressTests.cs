using Xunit;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// GameItemViewModel 进度卡生命周期（Phase 4b，2026-09-19，审计缺口 RunUpdateAsync 进度消费/完成清卡）：
/// 下载进度报告经 Progress&lt;UpdateProgress&gt; 异步转发进 OnProgress（百分比/阶段文案），完成后忙碌态
/// 释放（进度卡的可见性驱动源）；失败路径转状态行且不写登记状态。Progress 回调经线程池异步到达，
/// 一律有界轮询落地后再断言（StartupAssetPreloadTests 的孤儿任务竞态教训）。
/// </summary>
[Collection("sequential")]
public class GameItemProgressTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public GameItemProgressTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    /// <summary>给鸣潮渠道配置"已最新"的假清单与下载内容（两个文件，进度报告覆盖多文件聚合）。</summary>
    private void SetupKuroUpToDate()
    {
        var zipBytes = TestZip.Create(("Client/game.exe", "MZ"));
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.6.0" };
        _ctx.Kuro.Manifests["3.6.0"] = new GameManifest
        {
            Version = "3.6.0",
            Files =
            [
                new ManifestFile("Client/game.exe", zipBytes.Length, Hashing.Md5Hex(zipBytes), Url: "https://cdn/game.exe"),
                new ManifestFile("Client/Binaries/Win64/Client-Win64-Shipping.exe", zipBytes.Length, Hashing.Md5Hex(zipBytes), Url: "https://cdn/game.exe"),
            ],
        };
        _ctx.Downloader.Responses["https://cdn/game.exe"] = zipBytes;
    }

    [Fact]
    public async Task Install_ProgressConsumed_AndCardClearedOnCompletion()
    {
        SetupKuroUpToDate();
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);

        // 最后一次（Done 阶段）报告落地：百分比满格 + 阶段文案为"完成"
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (wuwa.ProgressText != "完成" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal("完成", wuwa.ProgressText);
        Assert.Equal(100, wuwa.ProgressPercent);
        Assert.False(wuwa.IsBusy); // 完成清卡：忙碌态释放
        Assert.Equal("已是最新版本", wuwa.StatusText); // 非校验语义：状态行交还 RefreshAsync
    }

    [Fact]
    public async Task Install_FailedMidDownload_ReportsFailure_ClearsCardAndKeepsUnregistered()
    {
        SetupKuroUpToDate();
        _ctx.Downloader.FailUrls.Add("https://cdn/game.exe");
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);

        Assert.StartsWith("失败：", wuwa.StatusText, StringComparison.Ordinal);
        Assert.Contains("假下载失败", wuwa.StatusText, StringComparison.Ordinal);
        Assert.False(wuwa.IsBusy);
        Assert.False(wuwa.IsInstalled); // 失败不落登记状态：下次启动仍提示安装
        Assert.Null(new LocalStateService(wuwa.InstallDirPath).Load(wuwa.Game.Id, "cn"));
    }
}
