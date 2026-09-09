using System.Text.Json;
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

    /// <summary>给鸣潮渠道配置"已安装即最新"的假清单与下载内容（含真实 exe 路径，安装后即可启动）。</summary>
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
    public async Task DetectExistingInstall_AllowsDirectLaunch()
    {
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];

        // 未登记（无 .yagl/state.json）且无游戏文件：不可启动
        Assert.False(wuwa.CanLaunch);
        Assert.Equal("尚未安装", wuwa.StatusText);

        // 模拟"来自官方启动器的既有安装"：游戏文件在，但没有启动器登记
        var exePath = Path.Combine(wuwa.InstallDirPath, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        await File.WriteAllBytesAsync(exePath, "MZ"u8.ToArray());
        await wuwa.RefreshAsync();

        Assert.False(wuwa.IsInstalled);
        Assert.True(wuwa.CanLaunch);
        Assert.Equal("检测到游戏文件，可直接启动", wuwa.StatusText);
        Assert.True(wuwa.HasGachaEntry);
        Assert.Equal("校验修复", wuwa.InstallButtonText); // "安装游戏"消失：校验登记即可，不会重装
    }

    [Fact]
    public async Task ExecutableDraft_PickedPathSavedRelativeAndDetected()
    {
        var picker = new FakeFilePicker();
        using var ctx = VmFactory.Build(filePicker: picker);
        await ctx.Vm.InitializeAsync();
        var wuwa = ctx.Vm.Games[0];

        // 选一个与配置默认值不同的主程序路径，制造真实的"可执行文件变更"
        var exePath = Path.Combine(wuwa.InstallDirPath, "bin", "MyGame.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        await File.WriteAllBytesAsync(exePath, "MZ"u8.ToArray());
        picker.ExecutableResult = exePath;

        // 初始尚未登记（无 state.json）：不可启动
        Assert.False(wuwa.CanLaunch);

        await wuwa.LaunchSettings.BrowseExecutableCommand.ExecuteAsync(null);

        // 安装目录内的绝对路径换算为相对路径写回 games.json，可启动性即时刷新
        Assert.Equal("bin/MyGame.exe", wuwa.Game.Executable);
        Assert.True(wuwa.CanLaunch);
        Assert.True(wuwa.HasGachaEntry);
        Assert.Equal(exePath.Replace('\\', '/'), Path.GetFullPath(Path.Combine(wuwa.InstallDirPath, wuwa.Game.Executable.Replace('\\', '/'))).Replace('\\', '/'));
        var json = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.Contains("bin/MyGame.exe", json);
    }

    [Fact]
    public async Task ProxyRadios_MapToModesAndPersist()
    {
        await _ctx.Vm.InitializeAsync();
        _ctx.Vm.ShowSettingsCommand.Execute(null);
        var settings = (SettingsViewModel)_ctx.Vm.CurrentPage!;

        // 默认跟随系统：地址框禁用
        Assert.True(settings.ProxyFollowSystem);
        Assert.False(settings.IsProxyAddressEnabled);

        // radio 互斥
        settings.ProxyDirect = true;
        Assert.False(settings.ProxyFollowSystem);
        Assert.False(settings.ProxyManual);
        settings.ProxyManual = true;
        Assert.False(settings.ProxyDirect);
        Assert.True(settings.IsProxyAddressEnabled);

        settings.ProxyAddressDraft = "http://127.0.0.1:7890";
        await settings.SaveProxyCommand.ExecuteAsync(null);
        Assert.False(settings.ProxySave.Failed);
        Assert.Equal("Manual", ReadProxyMode(_ctx.ConfigPath), ignoreCase: true);
        Assert.Equal("http://127.0.0.1:7890", ReadProxyAddress(_ctx.ConfigPath));

        // 直连：忽略地址草稿
        settings.ProxyDirect = true;
        await settings.SaveProxyCommand.ExecuteAsync(null);
        Assert.Equal("None", ReadProxyMode(_ctx.ConfigPath), ignoreCase: true);
        Assert.Equal("", ReadProxyAddress(_ctx.ConfigPath));

        // 无效地址：手动保存失败
        settings.ProxyManual = true;
        settings.ProxyAddressDraft = "not-a-proxy";
        await settings.SaveProxyCommand.ExecuteAsync(null);
        Assert.True(settings.ProxySave.Failed);
        Assert.Equal("None", ReadProxyMode(_ctx.ConfigPath), ignoreCase: true);
    }

    private static string? ReadProxyMode(string configPath) =>
        ReadSettings(configPath)?.GetProperty("proxyMode").GetString();

    private static string ReadProxyAddress(string configPath)
    {
        var settings = ReadSettings(configPath);
        return settings is { } value && value.TryGetProperty("proxyAddress", out var address)
            ? address.GetString() ?? ""
            : "";
    }

    private static JsonElement? ReadSettings(string configPath)
    {
        var catalog = JsonDocument.Parse(File.ReadAllText(configPath)).RootElement;
        return catalog.TryGetProperty("settings", out var settings) ? settings : null;
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
