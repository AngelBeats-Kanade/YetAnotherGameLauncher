using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// GameItemViewModel 离线兜底分支（Phase 4b，2026-09-19，审计缺口 RefreshAsync catch 内 :266-284）：
/// 版本检测失败（断网/超时）时状态行、版本 chip、安装态与可启动性的兜底取值，
/// 以及登记版本命令对检测失败的报错路径（FakeChannel.VersionInfoError 注入）。
/// </summary>
[Collection("sequential")]
public class GameItemOfflineTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public GameItemOfflineTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    /// <summary>在鸣潮游戏安装目录里放主程序文件（模拟官方启动器装好的既有安装）。</summary>
    private static string WriteGameExecutable(string installDir)
    {
        var exePath = Path.Combine(installDir, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllBytes(exePath, "MZ"u8.ToArray());
        return exePath;
    }

    [Fact]
    public async Task RefreshAsync_Offline_InstalledGame_ShowsLocalChipAndKeepsState()
    {
        _ctx.Kuro.VersionInfoError = new HttpRequestException("离线（测试注入）");
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        // 预置登记状态与游戏文件：离线时两者都要被认出（本地版本 chip + 可启动）
        await new LocalStateService(wuwa.InstallDirPath).SaveAsync(
            new LocalGameState { GameId = wuwa.Game.Id, ServerId = "cn", Version = "3.5.0" });
        WriteGameExecutable(wuwa.InstallDirPath);

        await wuwa.RefreshAsync();

        Assert.Equal("无法连接服务器，版本信息不可用", wuwa.StatusText);
        Assert.True(wuwa.IsInstalled);
        Assert.True(wuwa.CanLaunch);
        Assert.False(wuwa.HasUpdate);
        Assert.False(wuwa.PredownloadAvailable);
        // 离线已安装：chip 退化为单段"本地 x"（无迁移段），且不提示未安装
        Assert.Equal("本地", wuwa.VersionChipLead);
        Assert.Equal("3.5.0", wuwa.VersionChipNumber);
        Assert.Equal("", wuwa.VersionChipMid);
        Assert.Equal("", wuwa.VersionChipTarget);
        Assert.Equal("校验修复", wuwa.InstallButtonText);
    }

    [Fact]
    public async Task RefreshAsync_Offline_Uninstalled_ShowsNotInstalledChip()
    {
        _ctx.Kuro.VersionInfoError = new HttpRequestException("离线（测试注入）");
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        await wuwa.RefreshAsync();

        Assert.Equal("无法连接服务器，版本信息不可用", wuwa.StatusText);
        Assert.False(wuwa.IsInstalled);
        Assert.False(wuwa.CanLaunch); // 无游戏文件：离线也不可启动
        Assert.Equal("未安装", wuwa.VersionChipLead);
        Assert.Equal("", wuwa.VersionChipNumber);
        Assert.Equal("安装游戏", wuwa.InstallButtonText);
    }

    [Fact]
    public async Task RefreshAsync_Offline_DetectedInstall_EnablesLaunchWithoutRegistration()
    {
        // 检测到文件但未登记：离线兜底只看磁盘（CanLaunch=文件在），登记态不可得也不误报已安装
        _ctx.Gryphline.VersionInfoError = new HttpRequestException("离线（测试注入）");
        await _ctx.Vm.InitializeAsync();
        var endfield = _ctx.Vm.Games[1];
        var exePath = Path.Combine(endfield.InstallDirPath, "Endfield.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        await File.WriteAllBytesAsync(exePath, "MZ"u8.ToArray());

        await endfield.RefreshAsync();

        Assert.Equal("无法连接服务器，版本信息不可用", endfield.StatusText);
        Assert.False(endfield.IsInstalled);
        Assert.True(endfield.CanLaunch);
        Assert.Equal("未安装", endfield.VersionChipLead); // 未登记：离线 chip 走"未安装"而非最新版
    }

    [Fact]
    public async Task RefreshAsync_DownloadException_ShowsOfflineFallback_NotEscaping()
    {
        // HttpFileDownloader 重试耗尽后抛 DownloadException（直接继承 Exception，非 HttpRequestException），
        // 这是鸣潮渠道网络失败的真实异常形态（GetVersionInfoAsync → FetchTextAsync → downloader.DownloadFileAsync
        // → throw lastError）——离线兜底必须同样生效，异常不得穿出 RefreshAsync
        //（方法注释自述"任何异常都不允许穿出"，此前过滤器漏掉该类型即违背）
        _ctx.Kuro.VersionInfoError = new DownloadException("下载失败（HttpFileDownloader 重试耗尽形态）");
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        await wuwa.RefreshAsync();

        Assert.Equal("无法连接服务器，版本信息不可用", wuwa.StatusText);
        Assert.False(wuwa.HasUpdate);
        Assert.False(wuwa.PredownloadAvailable);
    }

    [Fact]
    public async Task RegisterVersion_VersionCheckFails_ReportsFailureAndStaysUnregistered()
    {
        _ctx.Gryphline.VersionInfoError = new HttpRequestException("离线（测试注入）");
        await _ctx.Vm.InitializeAsync();
        var endfield = _ctx.Vm.Games[1];
        var exePath = Path.Combine(endfield.InstallDirPath, "Endfield.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        await File.WriteAllBytesAsync(exePath, "MZ"u8.ToArray());
        await endfield.RefreshAsync(); // 检测到游戏文件 → 可登记

        await endfield.RegisterVersionCommand.ExecuteAsync(null);

        Assert.StartsWith("版本登记失败：", endfield.StatusText, StringComparison.Ordinal);
        Assert.False(endfield.IsBusy);
        Assert.False(endfield.IsInstalled);
        Assert.Null(new LocalStateService(endfield.InstallDirPath).Load(endfield.Game.Id, "global")); // 未写 state.json
    }
}
