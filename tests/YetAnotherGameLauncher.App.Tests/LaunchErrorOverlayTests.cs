using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 启动失败 UX：不可启动不再静默、预检失败弹主题化错误覆盖层
/// （类目化原因 + umu 引导安装按钮 + 可关闭），成功启动清掉残留覆盖层。
/// </summary>
[Collection("sequential")]
public class LaunchErrorOverlayTests
{
    private static VmFactory.Context BuildLinux() => VmFactory.Build(
        configJson: null,
        templateFactory: () => VmFactory.SampleConfigJson,
        platformInfo: new FakePlatformInfo(isLinux: true),
        linuxProtonVersions: []);

    [Fact]
    public async Task LaunchAsync_WhenNotReady_ShowsReasonInsteadOfSilentReturn()
    {
        using var ctx = BuildLinux();
        await ctx.Vm.InitializeAsync();
        var wuwa = ctx.Vm.Games[0];

        Assert.False(wuwa.CanLaunch);
        await wuwa.LaunchCommand.ExecuteAsync(null);

        Assert.False(string.IsNullOrWhiteSpace(wuwa.StatusText));
        Assert.Contains("主程序", wuwa.StatusText, StringComparison.Ordinal);
        Assert.Null(wuwa.LaunchError); // 未走到预检：不弹覆盖层
    }

    [Fact]
    public async Task LaunchAsync_RuntimeMissing_ShowsOverlayWithUmuInstall()
    {
        using var ctx = BuildLinux();
        await ctx.Vm.InitializeAsync();
        var wuwa = ctx.Vm.Games[0];
        await CreateGameExecutableAsync(wuwa); // 主程序就位：越过 ExecutableMissing，命中运行时预检
        wuwa.Game.Launch.CommandTemplate = "umu-run {exe}"; // 裸 umu-run：PATH 已禁用 → 运行时缺失

        await wuwa.LaunchCommand.ExecuteAsync(null);

        Assert.True(wuwa.HasLaunchError);
        Assert.NotNull(wuwa.LaunchError);
        Assert.Contains("umu-run", wuwa.LaunchError!.Message, StringComparison.Ordinal);
        Assert.True(wuwa.LaunchError.CanInstallUmu); // umu 模板失败 → 提供一键安装
        Assert.True(wuwa.LaunchError.HasDetail); // 技术详情可展开
        Assert.False(wuwa.LaunchError.HasLogPath); // 预检失败没有日志
    }

    [Fact]
    public async Task LaunchAsync_DismissHidesOverlay()
    {
        using var ctx = BuildLinux();
        await ctx.Vm.InitializeAsync();
        var wuwa = ctx.Vm.Games[0];
        await CreateGameExecutableAsync(wuwa);
        wuwa.Game.Launch.CommandTemplate = "umu-run {exe}";
        await wuwa.LaunchCommand.ExecuteAsync(null);
        Assert.True(wuwa.HasLaunchError);

        wuwa.LaunchError!.DismissCommand.Execute(null);

        Assert.False(wuwa.HasLaunchError);
    }

    [Fact]
    public async Task LaunchAsync_Success_ClearsStaleOverlay()
    {
        using var ctx = BuildLinux();
        await ctx.Vm.InitializeAsync();
        var wuwa = ctx.Vm.Games[0];
        wuwa.Game.Launch.CommandTemplate = "umu-run {exe}";
        await CreateGameExecutableAsync(wuwa);

        await wuwa.LaunchCommand.ExecuteAsync(null);
        Assert.True(wuwa.HasLaunchError);

        // 把模板换成直接运行：走成功路径（FakeProcessRunner 恒成功），残留覆盖层应被清掉
        wuwa.Game.Launch.CommandTemplate = "{exe}";

        await wuwa.LaunchCommand.ExecuteAsync(null);

        Assert.False(wuwa.HasLaunchError);
        Assert.Equal(wuwa.Loc["status_launched"], wuwa.StatusText);
    }

    /// <summary>在临时安装目录里创建游戏主程序桩并刷新状态（使 CanLaunch 与预检的 exe 检查通过）。</summary>
    private static async Task CreateGameExecutableAsync(GameItemViewModel game)
    {
        var exePath = Path.Combine(game.InstallDirPath, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllText(exePath, "MZ");
        await game.RefreshAsync();
    }
}
