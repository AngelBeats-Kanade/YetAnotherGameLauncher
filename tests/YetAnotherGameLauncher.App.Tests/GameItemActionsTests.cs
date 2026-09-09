using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>GameItemViewModel 操作语义测试：抽卡入口门控、预下载提示态、校验修复（文件式/包式）。</summary>
[Collection("sequential")]
public class GameItemActionsTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public GameItemActionsTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    /// <summary>给鸣潮渠道配置"已安装即最新"的假清单与下载内容。</summary>
    private void SetupKuroUpToDate()
    {
        var zipBytes = TestZip.Create(("Client/game.exe", "MZ"));
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.6.0" };
        _ctx.Kuro.Manifests["3.6.0"] = new GameManifest
        {
            Version = "3.6.0",
            Files = [new ManifestFile("Client/game.exe", zipBytes.Length, Hashing.Md5Hex(zipBytes), Url: "https://cdn/game.exe")],
        };
        _ctx.Downloader.Responses["https://cdn/game.exe"] = zipBytes;
    }

    /// <summary>给终末地渠道配置包式（整包压缩包）清单与下载内容。</summary>
    private void SetupEndfieldUpToDate()
    {
        var zipBytes = TestZip.Create(("bin/ef.exe", "MZ"));
        _ctx.Gryphline.VersionInfo = new ChannelVersionInfo { LatestVersion = "1.0.0" };
        _ctx.Gryphline.Manifests["1.0.0"] = new GameManifest
        {
            Version = "1.0.0",
            EntriesAreArchives = true,
            Files = [new ManifestFile("ef-1.0.0.zip", zipBytes.Length, Hashing.Md5Hex(zipBytes), Url: "https://cdn/ef.zip")],
        };
        _ctx.Downloader.Responses["https://cdn/ef.zip"] = zipBytes;
    }

    [Fact]
    public async Task GachaEntry_GatedOnInstallState()
    {
        await _ctx.Vm.InitializeAsync();

        // 未安装：鸣潮也不显示入口
        var wuwa = _ctx.Vm.Games[0];
        Assert.True(wuwa.IsKuro);
        Assert.False(wuwa.HasGachaEntry);

        SetupKuroUpToDate();
        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);
        Assert.True(wuwa.IsInstalled);
        Assert.True(wuwa.HasGachaEntry);

        // 终末地即便安装也不显示（非鸣潮渠道）
        SetupEndfieldUpToDate();
        var endfield = _ctx.Vm.Games[1];
        await endfield.InstallOrUpdateCommand.ExecuteAsync(null);
        Assert.True(endfield.IsInstalled);
        Assert.False(endfield.HasGachaEntry);
    }

    [Fact]
    public async Task PredownloadCue_ReflectsInstallAndUpdateState()
    {
        SetupKuroUpToDate();
        // SetupKuroUpToDate 会重设 VersionInfo，预下载窗口要在其后配置
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.6.0", PredownloadAvailable = true };
        await _ctx.Vm.InitializeAsync();

        var wuwa = _ctx.Vm.Games[0];
        Assert.False(wuwa.ShowPredownloadCue); // 未安装不提示

        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);
        Assert.True(wuwa.ShowPredownloadCue,
            $"installed={wuwa.IsInstalled} hasUpdate={wuwa.HasUpdate} predownload={wuwa.PredownloadAvailable} staged={wuwa.HasStagedPredownload} status={wuwa.StatusText}"); // 已安装且最新：进入提示态

        await wuwa.RefreshAsync();
        Assert.Equal("可预下载新版本", wuwa.StatusText);

        // 出现新版本：更新提示优先于预下载提示
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.7.0" };
        await wuwa.RefreshAsync();
        Assert.True(wuwa.HasUpdate);
        Assert.False(wuwa.ShowPredownloadCue);
        Assert.Equal("有可用更新", wuwa.StatusText);
    }

    [Fact]
    public async Task Verify_OnFileChannel_ReportsRepairedCount()
    {
        SetupKuroUpToDate();
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];
        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null); // 先安装

        // 同尺寸内容损坏（快速校验发现不了）：校验修复应经 MD5 事后校验补下载并报告修复数
        var exePath = Path.Combine(wuwa.InstallDirPath, "Client", "game.exe");
        var original = await File.ReadAllBytesAsync(exePath);
        var corrupted = (byte[])original.Clone();
        corrupted[0] ^= 0xFF;
        await File.WriteAllBytesAsync(exePath, corrupted);

        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);

        Assert.Equal("发现并修复 1 个文件", wuwa.StatusText);
        Assert.Equal(original, await File.ReadAllBytesAsync(exePath));
    }

    [Fact]
    public async Task Verify_OnPackageChannel_ConfirmThenReinstall()
    {
        SetupEndfieldUpToDate();
        await _ctx.Vm.InitializeAsync();
        var endfield = _ctx.Vm.Games[1];
        await endfield.InstallOrUpdateCommand.ExecuteAsync(null); // 首次安装不走确认
        Assert.True(endfield.IsInstalled);

        // 已安装且最新：包式渠道点击"校验修复"先弹确认条（修复 = 整包重下）
        await endfield.InstallOrUpdateCommand.ExecuteAsync(null);
        Assert.True(endfield.ShowRepairConfirm);
        Assert.Contains("完整安装包", endfield.RepairConfirmText);

        // 取消：只收起确认条，不下载
        var downloadsBefore = _ctx.Downloader.Requests.Count;
        endfield.CancelRepairCommand.Execute(null);
        Assert.False(endfield.ShowRepairConfirm);
        Assert.Equal(downloadsBefore, _ctx.Downloader.Requests.Count);

        // 确认：整包重下并校验，结果消息为"重装完成"
        await endfield.ConfirmRepairCommand.ExecuteAsync(null);
        Assert.False(endfield.ShowRepairConfirm);
        Assert.Equal("已重新下载并校验完整安装包", endfield.StatusText);
    }
}
