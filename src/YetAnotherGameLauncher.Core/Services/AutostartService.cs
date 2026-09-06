using System.Runtime.Versioning;
using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>开机自动启动：Windows 写 HKCU Run 注册表项，Linux 写 XDG autostart 桌面入口。</summary>
public interface IAutostartService
{
    bool IsEnabled();

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

public sealed class AutostartService(IProcessRunner runner) : IAutostartService
{
    private const string AppName = "YetAnotherGameLauncher";

    private static string ExePath =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("无法确定启动器可执行文件路径。");

    public bool IsEnabled() =>
        OperatingSystem.IsWindows() ? QueryWindows() : OperatingSystem.IsLinux() && File.Exists(DesktopFilePath());

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            await SetWindowsAsync(enabled, cancellationToken);
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

    private static bool QueryWindows()
    {
        var result = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "reg", $"query HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v {AppName}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        });
        result?.WaitForExit(5000);
        return result?.ExitCode == 0;
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
        await runner.RunAsync(spec, cancellationToken);
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
