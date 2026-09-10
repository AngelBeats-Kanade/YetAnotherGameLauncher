using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>Windows 平台环境：无 NVIDIA 探测（DXVK-NVAPI 仅 Linux 需要）；目录经 explorer 打开。</summary>
[ExcludeFromCodeCoverage] // 系统边界实现：行为由进程级真机验证覆盖
public sealed class WindowsPlatformInfo : IPlatformInfo
{
    /// <inheritdoc/>
    public bool IsLinux => false;

    /// <inheritdoc/>
    public bool IsNvidiaGpuPresent => false;

    /// <inheritdoc/>
    public IReadOnlyList<GpuVendor> GpuVendors => [];

    /// <inheritdoc/>
    public void OpenDirectoryInFileManager(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
}
