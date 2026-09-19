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

        // 进度文案经 ObservableProperty 逐次通知（线程池异步到达）：订阅历史记录而非只看终值，
        // 否则 CleaningUp/Verifying 这类中间阶段臂被终值覆盖、永远断言不到
        var phaseHistory = new List<string>();
        wuwa.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "ProgressText")
            {
                lock (phaseHistory)
                {
                    phaseHistory.Add(wuwa.ProgressText);
                }
            }
        };

        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);

        // 排空等待：终值"完成"出现且历史连续两轮无新增（Progress 回调落地时机不定，等队列安静）
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var lastCount = -1;
        while (DateTime.UtcNow < deadline)
        {
            int count;
            lock (phaseHistory)
            {
                count = phaseHistory.Count;
            }

            if (wuwa.ProgressText == "完成" && count == lastCount)
            {
                break;
            }

            lastCount = count;
            await Task.Delay(50);
        }

        Assert.Equal("完成", wuwa.ProgressText);
        Assert.Equal(100, wuwa.ProgressPercent);
        Assert.False(wuwa.IsBusy); // 完成清卡：忙碌态释放
        Assert.Equal("已是最新版本", wuwa.StatusText); // 非校验语义：状态行交还 RefreshAsync

        // 文件式渠道 SyncAsync 的固定阶段序列：Checking → Downloading → Verifying → CleaningUp → Done
        lock (phaseHistory)
        {
            Assert.Contains("正在检查文件…", phaseHistory);
            Assert.Contains("正在校验文件完整性…", phaseHistory);
            Assert.Contains("正在清理…", phaseHistory);
        }
    }

    [Fact]
    public async Task Install_PackageChannel_PatchingPhaseReported()
    {
        // 包式渠道（终末地）整包安装：解压阶段以 Patching 相位报告（"正在应用差分补丁…"文案臂）
        var zipBytes = TestZip.Create(("bin/ef.exe", "MZ"));
        _ctx.Gryphline.VersionInfo = new ChannelVersionInfo { LatestVersion = "1.0.0" };
        _ctx.Gryphline.Manifests["1.0.0"] = new GameManifest
        {
            Version = "1.0.0",
            EntriesAreArchives = true,
            Files = [new ManifestFile("ef-1.0.0.zip", zipBytes.Length, Hashing.Md5Hex(zipBytes), Url: "https://cdn/ef.zip")],
        };
        _ctx.Downloader.Responses["https://cdn/ef.zip"] = zipBytes;

        await _ctx.Vm.InitializeAsync();
        var endfield = _ctx.Vm.Games[1];

        var phaseHistory = new List<string>();
        endfield.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "ProgressText")
            {
                lock (phaseHistory)
                {
                    phaseHistory.Add(endfield.ProgressText);
                }
            }
        };

        await endfield.InstallOrUpdateCommand.ExecuteAsync(null);

        // 同上的排空等待
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var lastCount = -1;
        while (DateTime.UtcNow < deadline)
        {
            int count;
            lock (phaseHistory)
            {
                count = phaseHistory.Count;
            }

            if (endfield.ProgressText == "完成" && count == lastCount)
            {
                break;
            }

            lastCount = count;
            await Task.Delay(50);
        }

        Assert.Equal("完成", endfield.ProgressText);
        lock (phaseHistory)
        {
            Assert.Contains("正在应用差分补丁…", phaseHistory); // Patching 臂：包式解压阶段
        }
        Assert.True(endfield.IsInstalled);
    }

    [Fact]
    public async Task Install_ProgressFormats_KiloMegaGigaArms()
    {
        // 审计缺口（2026-09-19）：进度文案的字节格式化臂（KB/MB/GB）。三次单文件版本升级分别把
        // 声明字节数落在 KB/MB 档（内容真实、校验通过）；GB 档用"声明 3GB、实际小内容"驱动
        // （下载进度按声明字节数换算，无需真传 3GB），尺寸造假由事后校验识破、以"失败："收尾。
        // 单文件清单是关键：多文件批量的进度按累计字节报告，KB/MB 窗口会被跳过或乱序落地。
        await InitializeWithEmptyVersionAsync();
        var wuwa = _ctx.Vm.Games[0];

        var phaseHistory = new List<string>();
        wuwa.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "ProgressText")
            {
                lock (phaseHistory)
                {
                    phaseHistory.Add(wuwa.ProgressText);
                }
            }
        };

        UpdateTo("1.0.0", "a.bin", 2048, real: true); // KB
        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);
        UpdateTo("2.0.0", "b.bin", 2 << 20, real: true); // MB
        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);
        UpdateTo("3.0.0", "c.bin", 3L << 30, real: false); // GB（尺寸造假 → 失败）
        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);

        Assert.StartsWith("失败：", wuwa.StatusText, StringComparison.Ordinal); // 尺寸造假被事后校验识破

        // 三档单位都在下载文案里出现过（数字与单位间是不换行空格 U+00A0）
        lock (phaseHistory)
        {
            Assert.Contains(phaseHistory, t => t.Contains("\u00A0KB", StringComparison.Ordinal));
            Assert.Contains(phaseHistory, t => t.Contains("\u00A0MB", StringComparison.Ordinal));
            Assert.Contains(phaseHistory, t => t.Contains("\u00A0GB", StringComparison.Ordinal));
        }
    }

    /// <summary>以占位版本初始化 VM（真实版本由 <see cref="UpdateTo"/> 按轮次注入）。</summary>
    private async Task InitializeWithEmptyVersionAsync()
    {
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "0.0.0" };
        await _ctx.Vm.InitializeAsync();
    }

    /// <summary>注入下一轮版本的清单（real=false 时声明尺寸大于实际内容，驱动 GB 档文案并令校验失败）。</summary>
    private void UpdateTo(string version, string fileName, long declaredSize, bool real)
    {
        var content = real ? new byte[declaredSize] : TestZip.Create(("Client/game.exe", "MZ"));
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = version };
        _ctx.Kuro.Manifests[version] = new GameManifest
        {
            Version = version,
            Files = [new ManifestFile(fileName, declaredSize, Hashing.Md5Hex(content), Url: $"https://cdn/{version}")],
        };
        _ctx.Downloader.Responses[$"https://cdn/{version}"] = content;
        _ctx.Vm.Games[0].ResetVersionCheckCache();
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
