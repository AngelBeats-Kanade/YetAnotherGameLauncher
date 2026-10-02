using Xunit;

using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 启动参数端到端回合：UI 启动设置卡编辑（安装目录/可执行文件/命令模板/工作目录/环境变量）
/// → SaveAsync 落盘 games.json → 从磁盘重载目录 → GameLauncherService.BuildPlan 生成的
/// 最终命令行/参数/环境/工作目录与用户配置逐项一致（含含空格路径的 {exe} 引号展开）。
/// 参数按配置进入游戏的最短证明链。串行集合：与 headless 会话用户同队，避免并行调度踩中平台初始化竞态。
/// </summary>
[Collection("sequential")]
public class LaunchParameterRoundTripTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public LaunchParameterRoundTripTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task SavedLaunchSettings_DriveBuildPlanExactly()
    {
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];

        // 安装目录里放一个真实存在的可执行文件（目录名含空格，兼测 {exe} 引号展开）
        var installDir = _ctx.TempDir.FilePath("games-root", "My Game");
        Directory.CreateDirectory(Path.Combine(installDir, "bin"));
        // 配置规范：games.json 的相对路径统一正斜杠（SaveAsync 经 NormalizeDirectoryPath 归一）；
        // BuildPlan 经 GetFullPath 产出原生分隔符路径，期望值必须用同一归一表达式构造
        // （Windows 上 Path.Combine 不改写已含 '/' 的段，裸 Combine 会得到混合分隔符）
        var exeRelative = "bin/Game.exe";
        var exePath = Path.GetFullPath(Path.Combine(installDir, exeRelative));
        await File.WriteAllTextAsync(exePath, "#!/bin/sh");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(exePath, UnixFileMode.UserExecute);
        }

        var launchSettings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true));

        // 用户在启动设置卡里填的参数（草稿态）
        launchSettings.InstallDirDraft = installDir;
        launchSettings.ExecutableDraft = exeRelative;
        launchSettings.CommandTemplate = "{exe} --full-screen -resolution 1920x1080";
        launchSettings.WorkingDirectory = "{installDir}/saves";
        launchSettings.EnvironmentText = "MAP=\"coast 11\"\r\nPROTONPATH=DW-Proton"; // 值含空格须加引号（2026-10-03 Steam 风格词法）

        await launchSettings.SaveCommand.ExecuteAsync(null);
        Assert.False(launchSettings.Save.Failed);

        // 从磁盘重载（新 GameCatalogService 指向同一配置文件）：持久化确实是参数的事实源
        var reloader = new GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        var savedGame = reloader.Catalog!.Games[0]; // 样例目录含两个游戏，被编辑的是 Games[0]（鸣潮）
        Assert.Equal(installDir, savedGame.InstallDir);
        Assert.Equal(exeRelative, savedGame.Executable);
        Assert.Equal("{exe} --full-screen -resolution 1920x1080", savedGame.Launch.CommandTemplate);
        Assert.Equal("{installDir}/saves", savedGame.Launch.WorkingDirectory);
        Assert.Equal("coast 11", savedGame.Launch.Environment["MAP"]);
        Assert.Equal("DW-Proton", savedGame.Launch.Environment["PROTONPATH"]);

        // 重载后的参数直接喂 BuildPlan：最终命令行/工作目录/环境与用户配置逐项一致
        var runner = new FakeProcessRunner();
        var launcher = new GameLauncherService(runner, logDirectory: _ctx.TempDir.FilePath("logs"), pathValue: "");
        var plan = launcher.BuildPlan(savedGame, savedGame.InstallDir, savedGame.Executable);

        Assert.Equal(exePath, plan.FileName);
        Assert.Equal("--full-screen -resolution 1920x1080", plan.Arguments);
        // {installDir} 展开是字面替换，保留配置模板里的正斜杠，不做分隔符归一
        Assert.Equal(installDir + "/saves", plan.WorkingDirectory);
        Assert.Equal("coast 11", plan.Environment["MAP"]);
        Assert.Equal("DW-Proton", plan.Environment["PROTONPATH"]);
    }

    [Fact]
    public async Task SavedLaunchOptionsWithCommandPlaceholder_DriveBuildPlanExactly()
    {
        // 2026-10-03 Steam 语义端到端：编辑框显式 %command% → launch.arguments 落盘 →
        // 重载 → BuildPlan 把参数追加在模板命令之后；重开设置卡回显占位符行
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];

        var installDir = _ctx.TempDir.FilePath("games-root", "My Game");
        Directory.CreateDirectory(Path.Combine(installDir, "bin"));
        var exeRelative = "bin/Game.exe";
        var exePath = Path.GetFullPath(Path.Combine(installDir, exeRelative));
        await File.WriteAllTextAsync(exePath, "#!/bin/sh");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(exePath, UnixFileMode.UserExecute);
        }

        var launchSettings = new LaunchSettingsViewModel(
            game.Game, game.InstallDirPath, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true));

        launchSettings.InstallDirDraft = installDir;
        launchSettings.ExecutableDraft = exeRelative;
        launchSettings.CommandTemplate = "{exe} --full-screen";
        launchSettings.EnvironmentText = "MAP=\"coast 11\"\r\n%command% -dx11 \"a b\"";

        await launchSettings.SaveCommand.ExecuteAsync(null);
        Assert.False(launchSettings.Save.Failed);

        // 从磁盘重载：environment 与 arguments 各自落盘、互不混写
        var reloader = new GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        var savedGame = reloader.Catalog!.Games[0];
        Assert.Equal("coast 11", savedGame.Launch.Environment["MAP"]);
        Assert.Equal(new[] { "-dx11", "a b" }, savedGame.Launch.Arguments);

        // 重载后的参数直接喂 BuildPlan：用户参数在模板自带参数之后
        var runner = new FakeProcessRunner();
        var launcher = new GameLauncherService(runner, logDirectory: _ctx.TempDir.FilePath("logs"), pathValue: "");
        var plan = launcher.BuildPlan(savedGame, savedGame.InstallDir, savedGame.Executable);

        Assert.Equal(exePath, plan.FileName);
        Assert.Equal("--full-screen -dx11 \"a b\"", plan.Arguments);
        Assert.Equal("coast 11", plan.Environment["MAP"]);

        // 重开设置卡：编辑框回显 = 环境区序列化 + %command% 参数行，与已存值一致不点亮脏标
        var reopened = new LaunchSettingsViewModel(
            savedGame, installDir, _ctx.CatalogService, game.Loc, game,
            platformInfo: new FakePlatformInfo(isLinux: true));
        Assert.Contains("MAP=\"coast 11\"", reopened.EnvironmentText, StringComparison.Ordinal);
        Assert.Contains("%command% -dx11 \"a b\"", reopened.EnvironmentText, StringComparison.Ordinal);
        Assert.False(reopened.IsDirty);
    }
}
