using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 资产加载代际回归（2026-09-20 三审）：LoadAssetsCoreAsync 曾复用刷新代际做丢弃判定——
/// 预加载在途时任何导航/刷新（版本未变、不重载资产）都会递增代际把预加载结果误杀，
/// 图标/背景丢失直到版本/区域再变；另需保住原始目标：快切语言时旧区域背景不得"后到先赢"。
/// 现用专用 _assetLoadGeneration：仅更新的资产加载可作废在途结果。
/// </summary>
[Collection("sequential")]
public class GameItemAssetGenerationTests : IDisposable
{
    private readonly VmFactory.Context _ctx;
    private readonly VmFactory.FakeVideoPlayer _player = new();

    public GameItemAssetGenerationTests()
    {
        _ctx = VmFactory.Build(videoPlayer: _player);
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" };
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task AssetLoad_RefreshWithoutReload_DoesNotDiscardInFlightLoad()
    {
        // 回归主体：第二次刷新（版本/区域均未变 → 不发射新资产加载）不得作废第一次的
        // 在途加载——旧代码（复用 _refreshGeneration）在此把加载结果丢弃，资产永不落位
        var video = _ctx.TempDir.FilePath("video-first.mp4");
        await File.WriteAllTextAsync(video, "fake");
        var gate = new TaskCompletionSource<BackdropSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? firstRegion = null;
        _ctx.KuroBackdrop.AsyncRequestResolver = request =>
        {
            firstRegion ??= request.Region;
            return request.Region == firstRegion ? gate.Task : throw new Xunit.Sdk.XunitException("不应发生第二次解析");
        };

        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        game.SetDetailActive(true);
        await game.RefreshAsync(); // 首刷：版本变化 → 资产加载挂起在解析器上
        await game.RefreshAsync(); // 复刷：版本缓存命中且区域未变 → 不发射新加载
        Assert.Equal(1, _ctx.KuroBackdrop.ResolveCount);

        gate.SetResult(new BackdropSource(video, BackdropKind.Video));

        // 在途加载的结果必须落位（视频路径进入起播）
        var played = false;
        for (var i = 0; i < 500 && !played; i++)
        {
            await Task.Delay(10);
            played = _player.PlayedPaths.Count == 1;
        }

        Assert.Equal([video], _player.PlayedPaths);
    }

    [Fact]
    public async Task AssetLoad_StaleRegionResult_DoesNotLandAfterNewerLoad()
    {
        // 原始目标的守卫：快切语言时先发（旧区域）的加载后完成，不得覆盖新一轮加载的结果。
        // 挂起点选 http 图标（解析器之前）：背景服务对同游戏的解析按信号量串行，
        // 挂在解析器上会让新一轮加载死锁在后面，构不成"新先旧后"的交错
        var videoEn = _ctx.TempDir.FilePath("video-en.mp4");
        var videoZh = _ctx.TempDir.FilePath("video-zh.mp4");
        await File.WriteAllTextAsync(videoEn, "fake");
        await File.WriteAllTextAsync(videoZh, "fake");
        var iconUrl = "https://cdn.example.com/icon.png";
        _ctx.BackgroundHandler.Map(iconUrl, new byte[] { 1, 2, 3 });
        var iconGate = _ctx.BackgroundHandler.GateFirstRequest(iconUrl);
        var resolvedRegions = new List<string>();
        _ctx.KuroBackdrop.AsyncRequestResolver = request =>
        {
            resolvedRegions.Add(request.Region);
            return Task.FromResult<BackdropSource?>(new BackdropSource(
                request.Region.StartsWith("zh", StringComparison.Ordinal) ? videoZh : videoEn, BackdropKind.Video));
        };

        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        game.Game.Icon = iconUrl; // http 图标：刷新链路的图标加载走可挂起的处理器
        game.SetDetailActive(true);
        await game.RefreshAsync(); // 旧区域加载：图标请求挂起（解析未到）

        _ctx.Vm.Loc.SetLanguage("en-US");
        await game.RefreshAsync(); // 新区域加载：图标直通 → en 视频起播

        var enPlayed = false;
        for (var i = 0; i < 500 && !enPlayed; i++)
        {
            await Task.Delay(10);
            enPlayed = _player.PlayedPaths.Contains(videoEn);
        }

        // 放行旧区域的图标请求：旧加载继续走到解析（zh 视频）——
        // 必须被代际门丢弃，不得再起播/覆盖（无门变异会让 zh 视频落到最后）
        _ctx.BackgroundHandler.ReleaseFirstRequest(iconUrl);
        await Task.Delay(300);

        Assert.True(enPlayed, $"新区域加载应已起播 en 视频; played=[{string.Join(",", _player.PlayedPaths)}] resolved=[{string.Join(",", resolvedRegions)}] culture={_ctx.Vm.Loc.EffectiveCulture}");
        Assert.Equal(videoEn, _player.PlayedPaths[^1]);
        Assert.DoesNotContain(videoZh, _player.PlayedPaths[..^1]);
    }
}
