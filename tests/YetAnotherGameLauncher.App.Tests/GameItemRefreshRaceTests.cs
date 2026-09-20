using Xunit;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// GameItemViewModel 刷新竞态回归（2026-09-20）：切服后旧服务器的在途检测结果
/// 曾照常写回，用旧服务器的安装态/更新态/版本 chip 覆盖新服务器状态——
/// 现由 RefreshAsync 代际门丢弃过期结果。
/// </summary>
[Collection("sequential")]
public class GameItemRefreshRaceTests : IDisposable
{
    /// <summary>双服务器配置（鸣潮 cn + global），其余同 VmFactory 样例。</summary>
    private const string TwoServerConfig = """
        {
          "settings": { "installRoot": "~/yagl-test-games", "theme": "Dark", "maxParallelDownloads": 4 },
          "games": [
            {
              "id": "wuthering-waves",
              "displayName": "鸣潮",
              "nameLocalized": { "zh-CN": "鸣潮", "en-US": "Wuthering Waves" },
              "channel": "kuro",
              "installDir": "WutheringWaves",
              "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
              "servers": [ { "id": "cn", "name": "国服" }, { "id": "global", "name": "国际服" } ]
            },
            {
              "id": "arknights-endfield",
              "displayName": "明日方舟：终末地",
              "nameLocalized": { "zh-CN": "明日方舟：终末地", "en-US": "Arknights: Endfield" },
              "channel": "hypergryph",
              "installDir": "ArknightsEndfield",
              "executable": "Endfield.exe",
              "servers": [ { "id": "global", "name": "国际服" } ]
            }
          ]
        }
        """;

    private readonly VmFactory.Context _ctx;

    /// <summary>cn 服务器版本检测的闸门：不放行则该检测一直挂在途（模拟慢网）。</summary>
    private readonly TaskCompletionSource _cnGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GameItemRefreshRaceTests()
    {
        _ctx = VmFactory.Build(configJson: TwoServerConfig);
        _ctx.Kuro.VersionInfoHandler = async (server, cancellationToken) =>
        {
            if (server.Id == "cn")
            {
                await _cnGate.Task.WaitAsync(cancellationToken);
            }

            return _ctx.Kuro.VersionInfo;
        };
    }

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task RefreshAsync_StaleServerResultArrivesAfterSwitch_DoesNotOverwriteNewServerState()
    {
        // InitializeAsync 触发的首刷是 cn，被闸门挂起（= 慢网的在途旧刷新）
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];
        Assert.Equal("cn", wuwa.SelectedServer.Id);

        // global 已登记为最新版；cn 无登记——两台服务器的安装态可区分
        //（旧 cn 刷新在闸门前就读过 state，此刻补写 global 状态不影响它）
        await new LocalStateService(wuwa.InstallDirPath).SaveAsync(
            new LocalGameState { GameId = "wuthering-waves", ServerId = "global", Version = "2.0.0" });
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" };

        // 切到 global：新刷新无闸门，立即完成并写入 global 的安装态
        wuwa.SelectedServer = wuwa.Servers[1];
        await WaitForAsync(() => wuwa.StatusText.Length > 0);
        Assert.True(wuwa.IsInstalled);
        Assert.Equal("已是最新版本", wuwa.StatusText);

        // 放行旧 cn 刷新：其结果晚于切服到达，必须被代际门丢弃
        _cnGate.SetResult();
        // 负向断言只能靠等待：窗口太紧会让慢 CI 上的旧写入逃过断言（对被 revert 的代码假绿）
        await Task.Delay(1000);

        Assert.True(wuwa.IsInstalled);
        Assert.Equal("已是最新版本", wuwa.StatusText);
        Assert.False(wuwa.HasUpdate);
    }

    /// <summary>有界轮询：条件成立即返回（超时后由后续断言给出失败信息）。</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Predownload_ResultFromOldServer_DoesNotOverwriteNewServerStatus()
    {
        // 回归（2026-09-20 复审）：操作完成消息归属发起时的服务器——RefreshAsync 有代际门，
        // 紧随其后的 StatusText 写入没有；切服后完成的操作会把旧服结果文案盖到新服状态行上
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];
        Assert.Equal("cn", wuwa.SelectedServer.Id);

        // cn 上发起预下载（闸门挂起 = 慢网在途），期间切到 global
        var operation = wuwa.PredownloadCommand.ExecuteAsync(null);
        wuwa.SelectedServer = wuwa.Servers[1];
        _cnGate.SetResult();
        await operation;

        // global 的状态行只由它自己的刷新写（未登记 → "未安装"），不得被旧服的
        // "预下载失败/完成"文案覆盖
        await WaitForAsync(() => wuwa.StatusText.Length > 0);
        Assert.False(
            wuwa.StatusText.Contains("预下载", StringComparison.Ordinal),
            $"新服务器状态行被旧服结果覆盖：{wuwa.StatusText}");
    }
}
