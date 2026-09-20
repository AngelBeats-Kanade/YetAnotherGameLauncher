using Xunit;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 唤取页装配回归（2026-09-20 复审）：VmFactory 测试对象图曾缺 gachaService（生产传真对象），
/// ShowGacha 的 null 门让唤取页在全部 VM 测试中不可达——测试镜像生产装配的缺口回归。
/// </summary>
[Collection("sequential")]
public class GachaPageWiringTests
{
    [Fact]
    public async Task ShowGacha_NavigatesToGachaPage_AndLoadsCachedRecords()
    {
        // 同时钉住 7461580 的"构造期载缓存"修复：新建 GachaViewModel 的 Records
        // 必须已含缓存记录，而不是等手动刷新
        var ctx = VmFactory.Build();
        try
        {
            var cacheDir = ctx.TempDir.FilePath("gacha-cache");
            Directory.CreateDirectory(cacheDir);
            // 缓存形状 camelCase（KuroGachaService.GachaJsonOptions 命名策略）
            await File.WriteAllTextAsync(Path.Combine(cacheDir, "wuthering-waves.json"), """
                { "records": [ { "time": "2026-09-01 12:00:00", "name": "维里奈", "qualityLevel": 5, "poolType": 1 } ] }
                """);

            await ctx.Vm.InitializeAsync();
            var game = ctx.Vm.Games[0];

            ctx.Vm.ShowGachaCommand.Execute(game);

            var gacha = Assert.IsType<GachaViewModel>(ctx.Vm.CurrentPage);
            Assert.Single(gacha.Records);
            Assert.Equal("维里奈", gacha.Records[0].Name);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    [Fact]
    public async Task LanguageSwitch_RebuildsGachaPage_KeepingCachedRecords()
    {
        // 7461580 修复的回归：唤取页激活时切语言，页面 VM 整体重建（换文案）且记录仍在
        var ctx = VmFactory.Build();
        try
        {
            var cacheDir = ctx.TempDir.FilePath("gacha-cache");
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllTextAsync(Path.Combine(cacheDir, "wuthering-waves.json"), """
                { "records": [ { "time": "2026-09-01 12:00:00", "name": "维里奈", "qualityLevel": 5, "poolType": 1 } ] }
                """);

            await ctx.Vm.InitializeAsync();
            ctx.Vm.ShowGachaCommand.Execute(ctx.Vm.Games[0]);
            var before = Assert.IsType<GachaViewModel>(ctx.Vm.CurrentPage);

            ctx.Vm.Loc.SetLanguage("en-US");

            var after = Assert.IsType<GachaViewModel>(ctx.Vm.CurrentPage);
            Assert.NotSame(before, after);
            Assert.Single(after.Records);
        }
        finally
        {
            ctx.Dispose();
        }
    }
}
