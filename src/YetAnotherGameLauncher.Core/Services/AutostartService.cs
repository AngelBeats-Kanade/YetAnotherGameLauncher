using System.Runtime.Versioning;
using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>开机自动启动：Windows 写 HKCU Run 注册表项，Linux 写 XDG autostart 桌面入口。</summary>
public interface IAutostartService
{
    /// <summary>查询当前是否已开启自启。异步：Windows 需起 reg 子进程，禁止在 UI 线程同步等待。</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

public sealed class AutostartService(IProcessRunner runner) : IAutostartService
{
    private readonly IProcessRunner _runner = runner;

    private const string AppName = "YetAnotherGameLauncher";

    private static string ExePath =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine the launcher executable path.");

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        OperatingSystem.IsWindows()
            ? await QueryWindowsAsync(cancellationToken).ConfigureAwait(false)
            : OperatingSystem.IsLinux() && File.Exists(DesktopFilePath());

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            await SetWindowsAsync(enabled, cancellationToken).ConfigureAwait(false);
        }
        else if (OperatingSystem.IsLinux())
        {
            SetLinux(enabled);
        }
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

    public static string DesktopFilePath(string? home = null)
    {
        var dir = Path.Combine(
            home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "autostart");
        return Path.Combine(dir, "yetanothergamelauncher.desktop");
    }

    private async Task<bool> QueryWindowsAsync(CancellationToken cancellationToken)
    {
        // 走 IProcessRunner：stderr 已被捕获，键不存在时不会向控制台透传错误文本。
        // ConfigureAwait(false)：调用方可能处于 UI 线程同步上下文，避免续体回流 UI 队列。
        var result = await _runner.RunAsync(
            new ProcessStartSpec(
                "reg",
                $"query HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v {AppName}",
                TimeoutMilliseconds: 5000),
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    [SupportedOSPlatform("windows")]
    private async Task SetWindowsAsync(bool enabled, CancellationToken cancellationToken)
    {
        var spec = enabled
            ? new ProcessStartSpec(
                "reg",
                $"add HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v {AppName} /t REG_SZ /d \"\\\"{ExePath}\\\"\" /f")
            : new ProcessStartSpec(
                "reg",
                $"delete HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v {AppName} /f");
        await _runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
    }

    private void SetLinux(bool enabled)
    {
        var path = DesktopFilePath();
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildDesktopContent(ExePath));
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
