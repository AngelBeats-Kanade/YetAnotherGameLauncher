using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 启动资产预热与一次性版本检测：
/// 1) InitializeAsync 后全部游戏图标已加载（不等到选中），版本检测每游戏恰一次；
/// 2) 来回切换选中不再触发版本检测（会话缓存）；
/// 3) 版本号变化触发背景解析器重调 + http 图标绕过缓存重取；版本不变时零网络零解析器调用。
/// </summary>
[Collection("sequential")]
public sealed class StartupAssetPreloadTests
{
    private const string IconUrl1 = "https://cdn.example/icon-wu.png";
    private const string IconUrl2 = "https://cdn.example/icon-en.png";
    private const string BackdropUrl = "https://cdn.example/backdrop.png";

    /// <summary>1×1 PNG。</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    /// <summary>双游戏配置（各带 http 图标），其余结构与 VmFactory 样例一致。</summary>
    private static string ConfigJson(string icon1, string icon2) => $$"""
        {
          "settings": { "installRoot": "~/yagl-test-games", "theme": "Dark", "maxParallelDownloads": 4 },
          "games": [
            {
              "id": "wuthering-waves",
              "displayName": "鸣潮",
              "nameLocalized": { "zh-CN": "鸣潮", "en-US": "Wuthering Waves" },
              "channel": "kuro",
              "icon": "{{icon1}}",
              "installDir": "WutheringWaves",
              "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
              "servers": [ { "id": "cn", "name": "国服" } ]
            },
            {
              "id": "arknights-endfield",
              "displayName": "明日方舟：终末地",
              "nameLocalized": { "zh-CN": "明日方舟：终末地", "en-US": "Arknights: Endfield" },
              "channel": "hypergryph",
              "icon": "{{icon2}}",
              "installDir": "ArknightsEndfield",
              "executable": "Endfield.exe",
              "servers": [ { "id": "global", "name": "国际服" } ]
            }
          ]
        }
        """;

    [Fact]
    public async Task InitializeAsync_PreloadsAllIcons_AndChecksVersionOncePerGame()
    {
        // Bitmap 解码依赖 Avalonia 引擎，需在 headless 会话内执行
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            using var ctx = VmFactory.Build(ConfigJson(IconUrl1, IconUrl2));
            ctx.BackgroundHandler.Map(IconUrl1, Png);
            ctx.BackgroundHandler.Map(IconUrl2, Png);

            await ctx.Vm.InitializeAsync();

            // 启动即全部游戏图标可见（Games[1] 从未被选中）
            Assert.True(ctx.Vm.Games[0].HasGameIcon);
            Assert.True(ctx.Vm.Games[1].HasGameIcon);

            // 每游戏恰一次版本检测（选中游戏 1 次 + 预热补齐其余）
            Assert.Equal(new[] { "cn" }, ctx.Kuro.VersionInfoRequests);
            Assert.Equal(new[] { "global" }, ctx.Gryphline.VersionInfoRequests);

            // 来回切换选中：版本检测走会话缓存，不再打网络
            ctx.Vm.GameNavSelection = ctx.Vm.Games[1];
            ctx.Vm.GameNavSelection = ctx.Vm.Games[0];

            Assert.Equal(new[] { "cn" }, ctx.Kuro.VersionInfoRequests);
            Assert.Equal(new[] { "global" }, ctx.Gryphline.VersionInfoRequests);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task VersionChange_RefetchesIconAndResolvesBackdrop_Unchanged_SkipsAll()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            using var ctx = VmFactory.Build(ConfigJson(IconUrl1, IconUrl2));
            ctx.BackgroundHandler.Map(IconUrl1, Png);
            ctx.BackgroundHandler.Map(IconUrl2, Png);
            ctx.BackgroundHandler.Map(BackdropUrl, Png);
            ctx.KuroBackdrop.Resolver = _ => new BackdropSource(BackdropUrl, BackdropKind.Image);

            await ctx.Vm.InitializeAsync();

            // 首轮：图标下载一次、背景解析一次（版本 2.0.0 记入磁盘缓存元数据）
            Assert.Equal(1, ctx.BackgroundHandler.Requests.Count(r => r.RequestUri == new Uri(IconUrl1)));
            Assert.Equal(1, ctx.KuroBackdrop.ResolveCount);

            // 版本不变：重复刷新零网络、零解析器调用（版本门控命中磁盘缓存）
            await ctx.Vm.Games[0].RefreshAsync();
            Assert.Equal(1, ctx.KuroBackdrop.ResolveCount);
            Assert.Equal(1, ctx.BackgroundHandler.Requests.Count(r => r.RequestUri == new Uri(IconUrl1)));

            // 版本变化：重新解析背景（地址未变不重下）+ http 图标绕过缓存强制重取
            ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.0.0" };
            await ctx.Vm.Games[0].RefreshAsync();

            Assert.Equal(2, ctx.KuroBackdrop.ResolveCount);
            Assert.Equal(2, ctx.BackgroundHandler.Requests.Count(r => r.RequestUri == new Uri(IconUrl1)));
            Assert.Equal(1, ctx.BackgroundHandler.Requests.Count(r => r.RequestUri == new Uri(BackdropUrl)));
        }, CancellationToken.None);
    }
}
