using Microsoft.Extensions.Logging;
using Xunit;
using YetAnotherGameLauncher.Core.Dependencies;
using YetAnotherGameLauncher.Core.Services;
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
    private static string UmuConfigJson => VmFactory.UmuSampleConfigJson;

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
        FakeDependencyInstaller installer, CapturingLogger? logger = null)
    {
        var ctx = VmFactory.Build(
            configJson: UmuConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            dependencyInstaller: installer,
            dependencyLogger: logger);
        ScaffoldReadyUmuEnvironment(ctx.TempDir);
        await ctx.Vm.InitializeAsync();
        ctx.Vm.SelectedGame = ctx.Vm.Games[0];
        ctx.Vm.ShowGameSettingsCommand.Execute(null);
        var section = Assert.IsType<GameSettingsViewModel>(ctx.Vm.CurrentPage).Dependencies;
        return (ctx, section);
    }

    [Fact]
    public async Task CancelOngoingInstall_TokenReachesInstaller_AndFeedbackShowsCancelled()
    {
        // D2：依赖安装接入取消——页销毁/应用退出经 CancelOngoingInstall 终止在途安装。
        // 变异核对：VM 不传令牌时假安装器的挂起对 Cancel 不动，兜底放行后安装成功完成，
        // 断言"已取消"即红
        var installer = new FakeDependencyInstaller { HangOnInstall = true };
        var (ctx, section) = await BuildReadyUmuSectionAsync(installer);
        try
        {
            var item = section.Items[0];
            var install = section.InstallAsync(item);
            for (var i = 0; i < 200 && !section.IsBusy; i++)
            {
                await Task.Delay(10);
            }

            Assert.True(section.IsBusy);
            section.CancelOngoingInstall();
            installer.ReleaseInstall(); // 兜底放行：令牌未接线时安装将成功而非取消
            await install.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(section.IsBusy);
            Assert.True(section.Feedback.Failed);
            Assert.Equal("已取消", section.Feedback.Message);
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    [Fact]
    public async Task TrayHide_DoesNotCancelOngoingInstall_ButRealQuitPathDoes()
    {
        // 0.1.3 review F3：驻留隐藏（closeAction=HideToTray 的关窗路径）刻意不取消在途
        // 依赖安装——"后台继续"是驻留与真退出的语义差分，此处锁死两个方向
        var installer = new FakeDependencyInstaller { HangOnInstall = true };
        var (ctx, section) = await BuildReadyUmuSectionAsync(installer);
        try
        {
            // 第一轮：在途安装下走驻留隐藏 → 放行后安装完整跑完、令牌未被取消
            var item = section.Items[0];
            var install = section.InstallAsync(item);
            for (var i = 0; i < 200 && !section.IsBusy; i++)
            {
                await Task.Delay(10);
            }

            Assert.True(section.IsBusy);
            ctx.Vm.OnWindowHiddenToTray(800, 600, false);
            installer.ReleaseInstall();
            await install.WaitAsync(TimeSpan.FromSeconds(5));
            // 差分判据用 Feedback（fake 的 CancelledObserved 只在"未取消完成"路径写入，
            // 取消路径 WaitAsync 直接抛 OCE 跳过赋值，无法判别）：隐藏若误触取消，
            // 此处将是"已取消"失败态而非安装成功
            Assert.False(section.Feedback.Failed); // 安装成功收尾而非"已取消"
            Assert.True(installer.MarkInstalled); // 假安装器仅在完整跑完后置已安装

            // 第二轮：真退出路径（OnWindowClosing 清理链的 VM 侧入口）→ 取消令牌到达安装器。
            // 第一轮成功把假安装器置为已安装（条目 CanInstall=false 会让 InstallAsync 早退），
            // 须重置安装态并刷新条目
            installer.MarkInstalled = false;
            section.Refresh();
            var second = section.InstallAsync(item);
            for (var i = 0; i < 200 && !section.IsBusy; i++)
            {
                await Task.Delay(10);
            }

            Assert.True(section.IsBusy);
            ctx.Vm.CancelOngoingDependencyInstall();
            installer.ReleaseInstall();
            await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(section.Feedback.Failed);
            Assert.Equal("已取消", section.Feedback.Message);
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    [Fact]
    public async Task LaunchModeChangedInSection_RecomputesDependencyAvailability()
    {
        // D3：设置页内切换启动方式时依赖区现场重算（此前重进页面才自愈，状态文案是刷新期快照）
        var (ctx, section) = await BuildReadyUmuSectionAsync(new FakeDependencyInstaller());
        try
        {
            Assert.True(section.IsVisible); // umu 模式 + 环境就绪

            var settings = Assert.IsType<GameSettingsViewModel>(ctx.Vm.CurrentPage);
            settings.LaunchSettings.SelectedLaunchMode =
                settings.LaunchSettings.LaunchModes.First(m => m.Mode == LaunchMode.Direct);

            // 类设计："不可用是可见状态而非报错"——IsVisible 门只看安装器/平台/条目数，
            // Direct 模式的表现 = 不可用原因行 + 条目不可点（非整区隐藏）
            Assert.True(section.IsVisible);
            Assert.NotNull(section.UnavailableReason);
            Assert.All(section.Items, i => Assert.False(i.CanInstall));
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    [Fact]
    public async Task Detach_UnsubscribesLaunchSettings_NoFurtherRecompute()
    {
        // D3 泄漏面：Detach 后分区 VM 不再被共享 LaunchSettings 的委托驱动
        //（页丢弃后 GC 可回收）；此后切换启动方式不得再触发重算
        var (ctx, section) = await BuildReadyUmuSectionAsync(new FakeDependencyInstaller());
        try
        {
            section.Detach();

            var settings = Assert.IsType<GameSettingsViewModel>(ctx.Vm.CurrentPage);
            settings.LaunchSettings.SelectedLaunchMode =
                settings.LaunchSettings.LaunchModes.First(m => m.Mode == LaunchMode.Direct);

            // Detach 后快照保持 Detach 时的状态（可见性未随 Direct 重算）
            Assert.True(section.IsVisible);
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
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
    public async Task Install_PrefixUnhealthy_ShowsActionableRepairMessage()
    {
        // F46：内置组件链接失效必须映射到专属可操作文案（更新兼容组件/重建 prefix），
        // 不得落入 deps_error_unexpected 兜底
        _installer.FailKind = DependencyFailureKind.PrefixUnhealthy;
        var (ctx, section) = await BuildReadyUmuSectionAsync(_installer);
        try
        {
            var item = Assert.Single(section.Items);

            await item.InstallCommand.ExecuteAsync(null);

            Assert.True(section.Feedback.Failed);
            Assert.Equal(
                "prefix 组件链接失效（兼容组件升级遗留）且自动修复未完成：请更新兼容组件后重试，" +
                "仍失败则删除该游戏 prefix 重建。",
                section.Feedback.Message); // 红落此断言：当前落入 unexpected 兜底
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    [Fact]
    public async Task Install_Failure_LogsExceptionDetail()
    {
        // F49：分类文案不含细节，异常消息（退出码/stderr/不可修计数）必须落日志——
        // 文案"请查看日志"不能是空话
        var logger = new CapturingLogger();
        _installer.FailKind = DependencyFailureKind.DownloadFailed;
        var (ctx, section) = await BuildReadyUmuSectionAsync(_installer, logger);
        try
        {
            var item = Assert.Single(section.Items);

            await item.InstallCommand.ExecuteAsync(null);

            Assert.True(section.Feedback.Failed);
            Assert.True(logger.Has(LogLevel.Warning, "fake failure")); // 红落此断言：当前不落日志
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    [Fact]
    public async Task Install_UnexpectedFailure_LogsRawException()
    {
        // F49：分类学兜底的漏网异常同样要落日志（warn + 原始异常）
        var logger = new CapturingLogger();
        _installer.ThrowRaw = new InvalidOperationException("boom");
        var (ctx, section) = await BuildReadyUmuSectionAsync(_installer, logger);
        try
        {
            var item = Assert.Single(section.Items);

            await item.InstallCommand.ExecuteAsync(null);

            Assert.True(section.Feedback.Failed);
            Assert.True(logger.Has(LogLevel.Warning, "boom")); // 红落此断言：当前不落日志
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    [Fact]
    public async Task Install_SecondClickWhileBusy_Ignored_AndButtonsDisabled()
    {
        _installer.HangOnInstall = true; // 挂起首次安装，制造真实的长任务窗口
        var (ctx, section) = await BuildReadyUmuSectionAsync(_installer);
        try
        {
            var item = Assert.Single(section.Items);

            var first = item.InstallCommand.ExecuteAsync(null);

            // 忙碌期：全部条目按钮置灰（不依赖点击门静默吞掉）、进度行在场
            Assert.True(section.IsBusy);
            Assert.All(section.Items, i => Assert.False(i.CanInstall));

            await item.InstallCommand.ExecuteAsync(null); // 忙碌期间的第二次点击必须被吞掉
            _installer.ReleaseInstall();
            await first;

            Assert.Equal(1, _installer.InstallCount);
            Assert.False(section.IsBusy);
            Assert.True(item.CanInstall); // 收尾恢复可点
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    /// <summary>分类学兜底：安装器逃出的未分类异常不得静默，必须落到失败槽。</summary>
    [Fact]
    public async Task Install_UnexpectedRawException_ShowsFallbackFailure()
    {
        _installer.ThrowRaw = new InvalidOperationException("boom");
        var (ctx, section) = await BuildReadyUmuSectionAsync(_installer);
        try
        {
            var item = Assert.Single(section.Items);

            await item.InstallCommand.ExecuteAsync(null);

            Assert.True(section.Feedback.Failed);
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
