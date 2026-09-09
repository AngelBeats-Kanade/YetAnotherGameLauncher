using System.Runtime.Versioning;
using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>开机自动启动（平台抽象）：Windows 写 HKCU Run 注册表项，Linux 写 XDG autostart 桌面入口。
/// 平台实现由 DI 按当前系统注入，业务代码（设置页开关）只依赖本接口。</summary>
public interface IAutostartService
{
    /// <summary>查询当前是否已开启自启。异步：Windows 需起 reg 子进程，禁止在 UI 线程同步等待。</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>查询或切换自启状态（平台相关：Windows 注册表 / Linux 桌面入口）。</summary>
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>Windows 自启动实现：经 reg 子进程维护 HKCU Run 项。</summary>
public sealed class WindowsAutostartService(IProcessRunner runner) : IAutostartService
{
    /// <summary>Run 项名称。</summary>
    public const string AppName = "YetAnotherGameLauncher";

    private static string ExePath =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine the launcher executable path.");

    /// <inheritdoc/>
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        // 走 IProcessRunner：stderr 已被捕获，键不存在时不会向控制台透传错误文本。
        // ConfigureAwait(false)：调用方可能处于 UI 线程同步上下文，避免续体回流 UI 队列。
        var result = await runner.RunAsync(
            new ProcessStartSpec(
                "reg",
                $"query HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v {AppName}",
                TimeoutMilliseconds: 5000),
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    /// <inheritdoc/>
    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var spec = enabled
            ? new ProcessStartSpec(
                "reg",
                $"add HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v {AppName} /t REG_SZ /d \"\\\"{ExePath}\\\"\" /f")
            : new ProcessStartSpec(
                "reg",
                $"delete HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v {AppName} /f");
        await runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Linux 自启动实现：维护 ~/.config/autostart 的 XDG 桌面入口文件。</summary>
public sealed class LinuxAutostartService(
    string? home = null,
    string? executablePath = null) : IAutostartService
{
    private readonly string? _home = home;

    private readonly string? _executablePath = executablePath;

    private string ExePath => _executablePath
        ?? Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine the launcher executable path.");

    /// <inheritdoc/>
    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(DesktopFilePath(_home)));

    /// <inheritdoc/>
    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var path = DesktopFilePath(_home);
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildDesktopContent(ExePath));
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>XDG autostart 桌面入口内容（纯函数便于测试）。</summary>
    public static string BuildDesktopContent(string executablePath) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=YetAnotherGameLauncher
        Exec="{executablePath}"
        X-GNOME-Autostart-enabled=true
        """ + Environment.NewLine;

    /// <summary>XDG autostart 桌面入口文件路径（默认取当前用户主目录，home 可注入测试）。</summary>
    public static string DesktopFilePath(string? home = null)
    {
        var dir = Path.Combine(
            home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "autostart");
        return Path.Combine(dir, "yetanothergamelauncher.desktop");
    }
}
