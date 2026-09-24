using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 资产加载代际回归（2026-09-20 三审）：LoadAssetsCoreAsync 曾复用刷新代际做丢弃判定——
/// 预加载在途时任何导航/刷新（版本未变、不重载资产）都会递增代际把预加载结果误杀，
/// 图标/背景丢失直到版本/区域再变；另需保住原始目标：快切语言时旧区域背景不得"后到先赢"。
/// 现用专用 _assetLoadGeneration：仅更新的资产加载可作废在途结果。
/// 2026-09-24 重设计：前版 StaleRegionResult 用例的原绿是双巧合——①resolver 判
/// StartsWith("zh") 而 RegionForLanguage 实产 "cn"/"global"（zh 分支死代码，两轮都解析 en）；
/// ②StubHttpHandler 的 ReleaseFirstRequest 是死代码（门键被 Remove，放行从未生效），旧加载
/// 永远挂在图标请求上不再前进。门控修复（StubHttpHandlerGateTests）+ 真实区域分流后，
/// 本用例把旧加载真实地钉在 poster 写入窗口（:508 复核之后、写入段之前的代际盲区）。
/// </summary>
[Collection("sequential")]
public class GameItemAssetGenerationTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public GameItemAssetGenerationTests()
    {
        _ctx = VmFactory.Build();
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" };
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>被观察游戏的独占播放器（本测试组只观察 Games[0] 的起播）。</summary>
    private VmFactory.FakeVideoPlayer Player => _ctx.Players[0];

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
            played = Player.PlayedPaths.Count == 1;
        }

        Assert.Equal([video], Player.PlayedPaths);
    }

    [Fact]
    public async Task AssetLoad_StaleRegionResult_DoesNotLandAfterNewerLoad()
    {
        // 原始目标的守卫：快切语言时先发（旧区域）的加载后完成，不得覆盖新一轮加载的结果。
        // 构造（2026-09-24 重设计，前版原绿是双巧合——见类头注释）：
        // - 视频直链不映射（404）→ 背景服务下载失败走"直链穿透"，两轮解析都把 http 直链
        //   交回 VM——旧加载因此恰好挂在 **VM 海报 LoadAsync（poster 窗口）**，即 :508 复核
        //   之后、写入段（BackgroundImage/_videoPath/StartVideoAsync）之前的代际盲区；
        //   挂在服务内部（如视频下载）不行：背景服务对同游戏解析按信号量串行，会把
        //   新一轮加载死锁在后面，构不成"新先旧后"的交错
        // - 区域分流用真实产出值（RegionForLanguage → "cn"/"global"，前版判 StartsWith("zh")
        //   是永不命中的死分支）；StubHttpHandler 首请求门控经本批修复后放行真实生效
        var videoEn = "https://cdn.example.com/video-en.mp4";
        var videoZh = "https://cdn.example.com/video-zh.mp4";
        var posterEn = "https://cdn.example.com/poster-en.png";
        var posterZh = "https://cdn.example.com/poster-zh.png";
        _ctx.BackgroundHandler.Map(posterEn, new byte[] { 1, 2, 3 });
        _ctx.BackgroundHandler.Map(posterZh, new byte[] { 4, 5, 6 });
        _ = _ctx.BackgroundHandler.GateFirstRequest(posterZh);
        var resolvedRegions = new List<string>();
        _ctx.KuroBackdrop.AsyncRequestResolver = request =>
        {
            resolvedRegions.Add(request.Region);
            var cn = request.Region == "cn";
            return Task.FromResult<BackdropSource?>(new BackdropSource(
                cn ? videoZh : videoEn, BackdropKind.Video, cn ? posterZh : posterEn));
        };

        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        game.SetDetailActive(true);
        await game.RefreshAsync(); // 旧区域加载（cn）：视频 404 穿透 → 挂在海报请求上

        // 在途可见性（round-4 教训）：轮询 handler 确认旧加载确实到达 poster 窗口再切换语言，
        // 否则"旧加载未到挂起点就换代"的竞态会让断言对无门代码假绿
        var pinned = false;
        for (var i = 0; i < 500 && !pinned; i++)
        {
            await Task.Delay(10);
            pinned = _ctx.BackgroundHandler.Requests.Any(r => r.RequestUri!.ToString() == posterZh);
        }
        Assert.True(pinned, $"旧区域加载应已挂起在海报请求上; resolved=[{string.Join(",", resolvedRegions)}]");

        _ctx.Vm.Loc.SetLanguage("en-US");
        await game.RefreshAsync(); // 新区域加载（global）：海报直通 → en 视频起播

        var enPlayed = false;
        for (var i = 0; i < 500 && !enPlayed; i++)
        {
            await Task.Delay(10);
            enPlayed = Player.PlayedPaths.Contains(videoEn);
        }

        // 放行旧区域的海报请求：旧加载继续走到写入段——必须被代际门丢弃，
        // 不得覆盖海报/不得以旧区域视频路径起播（无门变异会让 zh 视频落到最后）
        _ctx.BackgroundHandler.ReleaseFirstRequest(posterZh);
        await Task.Delay(300);

        Assert.True(enPlayed, $"新区域加载应已起播 en 视频; played=[{string.Join(",", Player.PlayedPaths)}] resolved=[{string.Join(",", resolvedRegions)}] culture={_ctx.Vm.Loc.EffectiveCulture}");
        Assert.Equal(videoEn, Player.PlayedPaths[^1]);
        Assert.DoesNotContain(videoZh, Player.PlayedPaths);
    }

    // 静态图窗口（else 分支）的代际门没有独立红绿用例——**已声明的盲区**（DEVELOPMENT.md §3.6）：
    // 无法经现有服务层把旧加载钉在 VM 的静态图 LoadAsync 上——GameBackdropService 会先自己
    // 下载静态图（TryDownloadAsync 对 Image/Video 一视同仁）再把本地路径交回 VM；挂住服务内
    // 下载则让同游戏的新一轮 ResolveAsync 死锁在 per-game semaphore 后面（本用例早期构造实锤，
    // enLanded 永假）；"服务下载失败→直链穿透"路径上门的首个 claim 又必被服务侧请求消耗，
    // VM 的请求永远第二个到。静态门与 poster 门同型同修（LoadAssetsCoreAsync else 分支），
    // 红绿由同构的 poster 窗口用例（上方）代为验证。
}
