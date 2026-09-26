using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.UiTests;

/// <summary>
/// 游戏设置页「依赖」卡视觉树回归：Linux 且有安装器装配时卡片在场、条目命令正确接线；
/// Windows（缺省假平台）整卡隐藏。触碰 VmFactory/HeadlessSession → 常驻 sequential 集合。
/// </summary>
[Collection("sequential")]
public class DependencySectionHeadlessTests
{
    [Fact]
    public async Task LinuxWithInstaller_CardVisible_CommandWired()
    {
        var installer = new FakeDependencyInstaller();
        var ctx = VmFactory.Build(
            configJson: VmFactory.SampleConfigJson.Replace(
                "\"executable\": \"Client/Binaries/Win64/Client-Win64-Shipping.exe\",",
                "\"executable\": \"Client/Binaries/Win64/Client-Win64-Shipping.exe\",\n" +
                "              \"launch\": { \"commandTemplate\": \"native-umu {exe}\" },"),
            platformInfo: new FakePlatformInfo(isLinux: true),
            dependencyInstaller: installer);
        try
        {
            // 搭建就绪环境（DW-Proton + 已初始化 prefix），让依赖卡以可装状态入场
            var dataHome = ctx.TempDir.FilePath("data-home");
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

            await ctx.Vm.InitializeAsync();

            var cardVisible = false;
            var commandWired = false;
            var sectionNull = true;

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
                sectionNull = dependencies is null;
                var card = window.GetVisualDescendants()
                    .OfType<Border>().FirstOrDefault(b => b.Name == "DependenciesCard");
                cardVisible = card is { IsVisible: true };

                // 条目安装按钮的命令必须绑定到区 VM 的条目命令（Command 接线回归，交互一致性纪律）
                var firstItem = dependencies?.Items.Count > 0 ? dependencies.Items[0] : null;
                var button = firstItem is null
                    ? null
                    : window.GetVisualDescendants()
                        .OfType<Button>().FirstOrDefault(b => b.Command == firstItem.InstallCommand);
                commandWired = button is not null;

                window.Close();
            }, CancellationToken.None);

            Assert.False(sectionNull);
            Assert.True(cardVisible, "Linux + 安装器装配下依赖卡应可见");
            Assert.True(commandWired, "安装按钮的 Command 未绑定条目 InstallCommand");
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
