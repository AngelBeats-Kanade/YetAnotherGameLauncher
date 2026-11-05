using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Core.Dependencies;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 游戏设置页「依赖」卡视觉树回归：Linux 且有安装器装配时卡片在场、两个条目命令正确接线；
/// Windows（缺省假平台）整卡隐藏。触碰 VmFactory/HeadlessSession → 常驻 sequential 集合。
/// </summary>
[Collection("sequential")]
public class DependencySectionHeadlessTests
{
    /// <summary>搭建就绪环境：DW-Proton（含 wine）+ 统一 prefix 的 drive_c（搭进 ctx 自己的 data-home）。</summary>
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

    [Fact]
    public async Task LinuxWithInstaller_CardVisible_CommandsWired()
    {
        var installer = new FakeDependencyInstaller();
        var ctx = VmFactory.Build(
            configJson: VmFactory.UmuSampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            dependencyInstaller: installer);
        try
        {
            ScaffoldReadyUmuEnvironment(ctx.TempDir);
            await ctx.Vm.InitializeAsync();

            var cardVisible = false;
            var commandWired = false;
            var itemCount = 0;

            await HeadlessSession.Instance.Dispatch(() =>
            {
                var window = new MainWindow { DataContext = ctx.Vm };
                window.Show();
                ctx.Vm.SelectedGame = ctx.Vm.Games[0];
                ctx.Vm.ShowGameSettingsCommand.Execute(null);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var settings = Assert.IsType<GameSettingsViewModel>(ctx.Vm.CurrentPage);
                var dependencies = settings.Dependencies;
                itemCount = dependencies.Items.Count;
                var card = window.GetVisualDescendants()
                    .OfType<Border>().FirstOrDefault(b => b.Name == "DependenciesCard");
                cardVisible = card is { IsVisible: true };

                // 每个条目的主按钮命令都必须绑定到条目 VM（Command 接线回归）
                commandWired = dependencies.Items.All(item => window.GetVisualDescendants()
                    .OfType<Button>()
                    .Any(b => b.Command == item.InstallCommand && b.IsVisible));

                window.Close();
            }, CancellationToken.None);

            Assert.Equal(2, itemCount);
            Assert.True(cardVisible, "Linux + 安装器装配下依赖卡应可见");
            Assert.True(commandWired, "安装按钮的 Command 未绑定全部条目");
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    /// <summary>
    /// 已安装态：还原按钮可见，真实指针点击触发 UninstallAsync；还原期间解析为不定进度。
    /// 走真实指针（VM 命令直调拦不住命中测试/可见性断裂）。
    /// </summary>
    [Fact]
    public async Task Installed_RestoreButton_ClickTriggersUninstall_ShowsIndeterminateProgress()
    {
        var installer = new FakeDependencyInstaller
        {
            StateOverride = DependencyStateKind.Installed,
            HangOnUninstall = true,
        };
        var ctx = VmFactory.Build(
            configJson: VmFactory.UmuSampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            dependencyInstaller: installer);
        try
        {
            ScaffoldReadyUmuEnvironment(ctx.TempDir);
            await ctx.Vm.InitializeAsync();

            var clicked = false;
            var sawBusy = false;
            var sawIndeterminate = false;
            var progressText = "";

            await HeadlessSession.Instance.Dispatch(() =>
            {
                var window = new MainWindow { DataContext = ctx.Vm };
                window.Show();
                ctx.Vm.SelectedGame = ctx.Vm.Games[0];
                ctx.Vm.ShowGameSettingsCommand.Execute(null);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var settings = Assert.IsType<GameSettingsViewModel>(ctx.Vm.CurrentPage);
                var section = settings.Dependencies;
                var item = Assert.Single(
                    section.Items, i => i.Manifest.Id == "richedit-msftedit");

                // 依赖卡在设置页下方：先滚进视口，否则指针落在窗口外（命中测试失败）
                window.GetVisualDescendants()
                    .OfType<Border>().First(b => b.Name == "DependenciesCard").BringIntoView();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var button = window.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(b => b.Command == item.UninstallCommand && b.IsVisible);
                Assert.NotNull(button);

                var center = button.TranslatePoint(
                    new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseMove(center);
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                clicked = installer.UninstallCount == 1;
                sawBusy = section.IsBusy;
                sawIndeterminate = section.IsProgressIndeterminate;
                progressText = section.ProgressText;

                // 放行挂起的卸载，泵到命令收尾（Dispatch 内保持无 await）
                installer.ReleaseUninstall();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (section.IsBusy && DateTime.UtcNow < deadline)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(1);
                }

                Assert.False(section.IsBusy);
                window.Close();
            }, CancellationToken.None);

            Assert.True(clicked, "还原按钮点击未触发 UninstallAsync");
            Assert.True(sawBusy, "还原期间依赖区未进入忙碌态");
            Assert.True(sawIndeterminate, "还原期间进度条应为不定进度");
            Assert.Equal("还原中…", progressText);
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }

    [Fact]
    public async Task Windows_DefaultPlatform_CardHidden()
    {
        var ctx = VmFactory.Build();
        try
        {
            await ctx.Vm.InitializeAsync();

            var cardHidden = false;

            await HeadlessSession.Instance.Dispatch(() =>
            {
                var window = new MainWindow { DataContext = ctx.Vm };
                window.Show();
                ctx.Vm.SelectedGame = ctx.Vm.Games[0];
                ctx.Vm.ShowGameSettingsCommand.Execute(null);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var card = window.GetVisualDescendants()
                    .OfType<Border>().FirstOrDefault(b => b.Name == "DependenciesCard");
                cardHidden = card is null || !card.IsVisible;

                window.Close();
            }, CancellationToken.None);

            Assert.True(cardHidden, "Windows 平台依赖卡应整卡隐藏");
        }
        finally
        {
            ctx.TempDir.Dispose();
        }
    }
}
