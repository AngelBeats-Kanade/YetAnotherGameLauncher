using Xunit;
using YetAnotherGameLauncher.Core.Dependencies;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 游戏设置页「依赖」区 VM 行为：可见性门控（平台/装配）、目标解析联动、
/// 安装触发与目标断言、失败分类文案、互斥门、已安装态。
/// 触碰 VmFactory（平台首初始化）→ 常驻 sequential 集合。
/// 每用例独立 context（临时目录互不共享，脚手架必须搭进本用例的 data-home）。
/// </summary>
[Collection("sequential")]
public class DependencySectionViewModelTests : IDisposable
{
    private readonly VmFactory.Context _ctx;
    private readonly FakeDependencyInstaller _installer = new();

    public DependencySectionViewModelTests()
    {
        _ctx = VmFactory.Build(
            platformInfo: new FakePlatformInfo(isLinux: true),
            dependencyInstaller: _installer);
    }

    public void Dispose() => _ctx.TempDir.Dispose();

    /// <summary>以原生 umu 启动方式落盘配置（其余字段复用样例）。</summary>
    private static string UmuConfigJson => VmFactory.SampleConfigJson.Replace(
        "\"executable\": \"Client/Binaries/Win64/Client-Win64-Shipping.exe\",",
        "\"executable\": \"Client/Binaries/Win64/Client-Win64-Shipping.exe\",\n" +
        "              \"launch\": { \"commandTemplate\": \"native-umu {exe}\" },");

    /// <summary>搭建"已就绪"的本地环境：DW-Proton（含 wine）+ 统一 prefix 的 drive_c（搭进 ctx 自己的 data-home）。</summary>
    private static void ScaffoldReadyUmuEnvironment(TempDir tempDir)
    {
        var dataHome = tempDir.FilePath("data-home");
        var wine = Path.Combine(
            dataHome, "Steam", "compatibilitytools.d", "dwproton-11.0-12", "files", "bin", "wine");
        Directory.CreateDirectory(Path.GetDirectoryName(wine)!);
        File.WriteAllText(wine, "#!/bin/sh\n");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(wine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.CreateDirectory(Path.Combine(
            dataHome, "yagl", "prefixes", "wuthering-waves", "pfx", "drive_c", "windows"));
    }

    /// <summary>构建 umu 配置 + 就绪环境 + 已注入安装器的上下文，并导航到游戏设置页返回依赖区。</summary>
    private async Task<(VmFactory.Context Ctx, DependencySectionViewModel Section)> BuildReadyUmuSectionAsync(
        FakeDependencyInstaller installer)
    {
        var ctx = VmFactory.Build(
            configJson: UmuConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            dependencyInstaller: installer);
        ScaffoldReadyUmuEnvironment(ctx.TempDir);
        await ctx.Vm.InitializeAsync();
        ctx.Vm.SelectedGame = ctx.Vm.Games[0];
        ctx.Vm.ShowGameSettingsCommand.Execute(null);
        var section = Assert.IsType<GameSettingsViewModel>(ctx.Vm.CurrentPage).Dependencies;
        return (ctx, section);
    }

    [Fact]
    public async Task NoInstaller_SectionHidden()
    {
        // 未注入安装器的上下文（镜像既有测试装配）
        var ctx = VmFactory.Build(platformInfo: new FakePlatformInfo(isLinux: true));
        try
        {
            await ctx.Vm.InitializeAsync();
            ctx.Vm.SelectedGame = ctx.Vm.Games[0];
            ctx.Vm.ShowGameSettingsCommand.Execute(null);
            var settings = Assert.IsType<GameSettingsViewModel>(ctx.Vm.CurrentPage);

            Assert.False(settings.Dependencies.IsVisible);
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    [Fact]
    public async Task UnresolvableTarget_ShowsUnavailableReason_ItemNotInstallable()
    {
        // 未搭建 Proton/prefix：依赖区整体不可用，条目不可点
        await _ctx.Vm.InitializeAsync();
        _ctx.Vm.SelectedGame = _ctx.Vm.Games[0];
        _ctx.Vm.ShowGameSettingsCommand.Execute(null);
        var section = Assert.IsType<GameSettingsViewModel>(_ctx.Vm.CurrentPage).Dependencies;

        Assert.True(section.IsVisible);
        Assert.NotNull(section.UnavailableReason);
        Assert.NotEmpty(section.UnavailableReason);
        Assert.All(section.Items, i => Assert.False(i.CanInstall));
    }

    [Fact]
    public async Task UmuMode_ReadyEnvironment_ItemInstallable_NotInstalled()
    {
        var (ctx, section) = await BuildReadyUmuSectionAsync(_installer);
        try
        {
            Assert.Null(section.UnavailableReason);
            var item = Assert.Single(section.Items);
            Assert.True(item.CanInstall);
            Assert.False(item.IsInstalled);
            Assert.Equal("未安装", item.StatusText);
            Assert.Equal("安装", item.ButtonText);
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    [Fact]
    public async Task Install_InvokesInstallerWithResolvedTarget_RefreshesToInstalled()
    {
        var (ctx, section) = await BuildReadyUmuSectionAsync(_installer);
        try
        {
            var item = Assert.Single(section.Items);

            await item.InstallCommand.ExecuteAsync(null);

            // 目标由解析器现场解析：umu 布局的 pfx prefix + Proton 内置 wine
            Assert.NotNull(_installer.ReceivedTarget);
            Assert.EndsWith(
                Path.Combine("yagl", "prefixes", "wuthering-waves", "pfx"),
                _installer.ReceivedTarget.WinePrefixDirectory);
            Assert.EndsWith(
                Path.Combine("files", "bin", "wine"),
                _installer.ReceivedTarget.WineExecutable);

            // 装完回读状态：成功文案 + 已安装 + 按钮变重装
            Assert.True(_installer.InstallRequested);
            Assert.True(section.Feedback.HasMessage);
            Assert.False(section.Feedback.Failed);
            Assert.True(item.IsInstalled);
            Assert.Equal("重装", item.ButtonText);
            Assert.False(section.IsBusy);
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    [Fact]
    public async Task Install_Failure_ShowsMappedErrorAndStaysInstallable()
    {
        _installer.FailKind = DependencyFailureKind.DownloadFailed;
        var (ctx, section) = await BuildReadyUmuSectionAsync(_installer);
        try
        {
            var item = Assert.Single(section.Items);

            await item.InstallCommand.ExecuteAsync(null);

            Assert.True(section.Feedback.Failed);
            Assert.False(item.IsInstalled);
            Assert.False(section.IsBusy);
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    [Fact]
    public async Task Install_SecondClickWhileBusy_Ignored()
    {
        _installer.HangOnInstall = true; // 挂起首次安装，制造真实的长任务窗口
        var (ctx, section) = await BuildReadyUmuSectionAsync(_installer);
        try
        {
            var item = Assert.Single(section.Items);

            var first = item.InstallCommand.ExecuteAsync(null);
            await item.InstallCommand.ExecuteAsync(null); // 忙碌期间的第二次点击必须被吞掉
            _installer.ReleaseInstall();
            await first;

            Assert.Equal(1, _installer.InstallCount);
            Assert.False(section.IsBusy);
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    [Fact]
    public async Task AlreadyInstalled_ShowsVersionAndReinstall()
    {
        _installer.MarkInstalled = true;
        var (ctx, section) = await BuildReadyUmuSectionAsync(_installer);
        try
        {
            var item = Assert.Single(section.Items);

            Assert.True(item.IsInstalled);
            Assert.Contains("2.004R", item.StatusText);
            Assert.Equal("重装", item.ButtonText);
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }
}
