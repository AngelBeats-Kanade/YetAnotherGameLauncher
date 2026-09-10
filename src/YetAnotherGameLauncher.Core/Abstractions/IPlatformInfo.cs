namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>
/// 平台环境抽象：集中操作系统差异（Linux 探测、GPU 探测、文件管理器打开目录），
/// 业务代码只依赖本接口，Windows/Linux 各给实现（DI 按当前系统注入），测试注入假实现。
/// </summary>
public interface IPlatformInfo
{
    /// <summary>当前是否为 Linux（决定 Proton/Wine 兼容层等 Linux 专属能力是否可用）。</summary>
    bool IsLinux { get; }

    /// <summary>是否存在 NVIDIA 显卡（Linux 读 /proc/driver/nvidia/version；Windows 恒 false，无需 DXVK-NVAPI）。</summary>
    bool IsNvidiaGpuPresent { get; }

    /// <summary>
    /// 探测到的 GPU 厂商集合（去重）。
    /// Linux 合并两路探测：/proc 闭源驱动（NVIDIA，与 <see cref="IsNvidiaGpuPresent"/> 同源）
    /// 与 /sys/class/drm 的 PCI vendor（三家都覆盖）；Windows 恒空集合（无需此信息）。
    /// </summary>
    IReadOnlyList<GpuVendor> GpuVendors { get; }

    /// <summary>用系统的文件管理器打开目录（Windows explorer / macOS open / Linux xdg-open）。</summary>
    void OpenDirectoryInFileManager(string path);
}
