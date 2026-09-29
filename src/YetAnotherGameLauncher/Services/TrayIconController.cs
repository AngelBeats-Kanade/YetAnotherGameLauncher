using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.ViewModels;
using YetAnotherGameLauncher.Views;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 系统托盘驻留：常驻托盘图标（左键唤回主窗口）+ 原生菜单（显示主窗口/退出）。
/// Linux 走 D-Bus StatusNotifierItem（KDE 系统托盘原生协议，与显示协议无关，
/// 原生 Wayland 下可用）、Windows 走 Shell_NotifyIcon，均为 Avalonia 内建实现。
/// <para>
/// <b>线程规则</b>：SNI 菜单命令/图标点击回调到达 Tmds D-Bus 总线线程，直接触碰窗口/VM
/// 会段错误（2026-09 调研实锤）——所有回调必须先 <see cref="Dispatcher.UIThread"/> 投递回
/// UI 线程，且只投递同步 Action（<c>Dispatch(async</c> 禁用形态——发射后不管吞断言，CI grep 守卫）；
/// NativeMenuItem 走 Command（12.1.3 无公开 Clicked 事件，探针实证），执行线程同样
/// 未经上游文档保证，统一包裹。
/// </para>
/// 本类只做接线：可判定逻辑在 MainWindowViewModel（关闭判定）与 MainWindow（拦截/放行），
/// 故排除行覆盖。图标经 <c>Application.TrayIcon.Icons</c> 附加属性挂到应用上与应用同寿命，
/// 本实例自身无状态可被回收；菜单文案取构造时刻的语言，运行中切换语言不回填（重启生效）。
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class TrayIconController
{
    /// <summary>装配托盘图标与菜单（一次性接线，挂到 Application.TrayIcon.Icons）。</summary>
    public TrayIconController(MainWindow window, MainWindowViewModel viewModel)
    {
        var loc = viewModel.Loc;
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem
        {
            Header = loc["tray_show"],
            Command = new RelayCommand(() => Dispatcher.UIThread.Post(window.ShowFromTray)),
        });
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem
        {
            Header = loc["tray_exit"],
            Command = new RelayCommand(() => Dispatcher.UIThread.Post(window.RequestRealClose)),
        });

        var icon = new TrayIcon
        {
            Icon = LoadAppIcon(),
            ToolTipText = "YetAnotherGameLauncher",
            Menu = menu,
        };
        // 左键点击 = 唤回主窗口（菜单里另有显式入口）
        icon.Clicked += (_, _) => Dispatcher.UIThread.Post(window.ShowFromTray);

        // 构造发生在 OnFrameworkInitializationCompleted 内，Application.Current 必非空
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { icon });
    }

    /// <summary>从嵌入资源加载托盘图标：专用 48px 源（自 app-icon.png 高质量缩放派生）——
    /// SNI 托盘按 ~22-48px 显示，预缩放避免把 256px 大图交给合成器缩放。</summary>
    private static WindowIcon LoadAppIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://YetAnotherGameLauncher/Assets/tray-icon-48.png"));
        return new WindowIcon(stream);
    }
}
