using System.Text;
using YetAnotherGameLauncher.Channels.Kuro;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>唤取记录页 ViewModel：卡池选项、无地址引导、拉取合并与统计（经假渠道桩全离线）。</summary>
[Collection("sequential")]
public class GachaViewModelTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly StubHttpHandler _handler = new();
    private readonly VmFactory.Context _ctx;

    public GachaViewModelTests() => _ctx = VmFactory.Build();

    public void Dispose()
    {
        _ctx.Dispose();
        _tempDir.Dispose();
    }

    private const string SampleUrl = "https://aki-gm-resources.aki-game.com/aki/gacha/index.html" +
        "#/record?svr_id=76xx&player_id=100000002&lang=zh-Hans&gacha_id=xx&gacha_type=1" +
        "&svr_area=cn&record_id=3dc48935abc&resources_id=0bd52af4&platform=PC";

    private KuroGachaService CreateService() =>
        new(new HttpClient(_handler), _tempDir.FilePath("gacha"));

    private async Task<(VmFactory.Context Ctx, GachaViewModel ViewModel)> CreateInitializedViewModelAsync()
    {
        await _ctx.Vm.InitializeAsync();
        var viewModel = new GachaViewModel(_ctx.Vm, _ctx.Vm.Games[0], CreateService());
        return (_ctx, viewModel);
    }

    private void WriteGameLog()
    {
        var installDir = _ctx.Vm.Games[0].InstallDirPath;
        var logPath = Path.Combine(installDir, "Client", "Saved", "Logs", "Client.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, $"url=\"{SampleUrl}\"", Encoding.UTF8);
    }

    [Fact]
    public async Task Constructor_BuildsAllPoolOptions()
    {
        await _ctx.Vm.InitializeAsync();
        var viewModel = new GachaViewModel(_ctx.Vm, _ctx.Vm.Games[0], CreateService());
        Assert.Equal(8, viewModel.Pools.Count); // 全部 + 1-7
        Assert.Equal(0, viewModel.Pools[0].Type);
        Assert.Same(viewModel.Pools[0], viewModel.SelectedPool);
        Assert.Equal(_ctx.Vm.Games[0].DisplayName, viewModel.DisplayName);
    }

    [Fact]
    public async Task RefreshAsync_NoUrl_ShowsGuidanceHint()
    {
        var (_, viewModel) = await CreateInitializedViewModelAsync();

        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.IsStatusHint);
        Assert.Contains("唤取地址", viewModel.StatusText);
        Assert.Empty(viewModel.Records);
        Assert.Equal(0, viewModel.TotalCount);
    }

    [Fact]
    public async Task RefreshAsync_WithLogAndStub_FetchesMergesAndComputesStats()
    {
        var (_, viewModel) = await CreateInitializedViewModelAsync();
        WriteGameLog();
        // 每个池返回一页两条（少于一页下限即停）：五星在前、四星在后
        _handler.Map("https://gmserver-api.aki-game2.com/gacha/record/query", """
            {"code":0,"data":[
              {"time":"2026-09-01 10:00:00","name":"Char A","qualityLevel":5},
              {"time":"2026-09-01 09:00:00","name":"Weap B","qualityLevel":4}
            ]}
            """);

        await viewModel.RefreshAsync();

        Assert.Contains("14", viewModel.StatusText); // 7 池 × 2 条
        Assert.Equal(14, viewModel.TotalCount);
        Assert.Equal(7, viewModel.FiveStarCount);
        Assert.Equal(7, viewModel.FourStarCount);

        // 切换到 1 号池：只剩该池两条，五星在前 → 保底计数 0
        viewModel.SelectedPool = viewModel.Pools.First(p => p.Type == 1);
        Assert.Equal(2, viewModel.Records.Count);
        Assert.Equal(2, viewModel.TotalCount);
        Assert.Equal(1, viewModel.FiveStarCount);
        Assert.Equal(1, viewModel.FourStarCount);
        Assert.Equal(0, viewModel.SinceLastFiveStar);
        Assert.Equal("Char A", viewModel.Records[0].Name); // 时间倒序，五星在前
        Assert.True(viewModel.Records[0].Rare5);
        Assert.True(viewModel.Records[1].Rare4);
    }
}
