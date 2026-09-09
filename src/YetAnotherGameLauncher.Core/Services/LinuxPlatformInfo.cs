using System.Diagnostics;
using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// Linux 平台环境：经 /proc 探测 NVIDIA 显卡（路径可注入测试）；目录经 xdg-open 打开。
/// </summary>
public sealed class LinuxPlatformInfo : IPlatformInfo
{
    /// <summary>NVIDIA 探测文件默认路径；测试可注入临时文件。</summary>
    private readonly string _procNvidiaVersionPath;

    public LinuxPlatformInfo(string? procNvidiaVersionPath = "/proc/driver/nvidia/version")
    {
        _procNvidiaVersionPath = procNvidiaVersionPath ?? "/proc/driver/nvidia/version";
    }

    /// <inheritdoc/>
    public bool IsLinux => true;

    /// <inheritdoc/>
    public bool IsNvidiaGpuPresent => File.Exists(_procNvidiaVersionPath);

    /// <inheritdoc/>
    public void OpenDirectoryInFileManager(string path) =>
        Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
}
