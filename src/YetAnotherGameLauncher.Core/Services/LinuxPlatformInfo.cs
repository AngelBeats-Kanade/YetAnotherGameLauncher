using System.Diagnostics;
using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// Linux 平台环境：经 /proc 探测 NVIDIA 闭源驱动、经 /sys/class/drm 探测全部 GPU 厂商
/// （路径均可注入测试）；目录经 xdg-open 打开。
/// </summary>
public sealed class LinuxPlatformInfo : IPlatformInfo
{
    /// <summary>NVIDIA 闭源驱动探测文件默认路径；测试可注入临时文件。</summary>
    private readonly string _procNvidiaVersionPath;

    /// <summary>DRM 设备树默认根目录；测试可注入临时目录。</summary>
    private readonly string _sysfsDrmRoot;

    public LinuxPlatformInfo(
        string? procNvidiaVersionPath = "/proc/driver/nvidia/version",
        string? sysfsDrmRoot = "/sys/class/drm")
    {
        _procNvidiaVersionPath = procNvidiaVersionPath ?? "/proc/driver/nvidia/version";
        _sysfsDrmRoot = sysfsDrmRoot ?? "/sys/class/drm";
    }

    /// <inheritdoc/>
    public bool IsLinux => true;

    /// <inheritdoc/>
    public bool IsNvidiaGpuPresent => File.Exists(_procNvidiaVersionPath);

    /// <inheritdoc/>
    public IReadOnlyList<GpuVendor> GpuVendors
    {
        get
        {
            var vendors = new HashSet<GpuVendor>();
            if (IsNvidiaGpuPresent)
            {
                vendors.Add(GpuVendor.Nvidia);
            }

            // 直接枚举 card* 目录再查 device/vendor，不全局递归：
            // /sys/class/drm 下还有大量 card*-<连接器> 符号链接，递归会踩到它们
            if (Directory.Exists(_sysfsDrmRoot))
            {
                foreach (var cardDir in Directory.EnumerateDirectories(_sysfsDrmRoot, "card*"))
                {
                    // 连接器目录没有 device/vendor，File.Exists 过滤掉
                    var vendorFile = Path.Combine(cardDir, "device", "vendor");
                    if (TryMapVendorId(File.Exists(vendorFile) ? ReadVendorId(vendorFile) : null) is { } vendor)
                    {
                        vendors.Add(vendor);
                    }
                }
            }

            return vendors.OrderBy(v => v).ToArray();
        }
    }

    /// <inheritdoc/>
    public void OpenDirectoryInFileManager(string path) =>
        Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });

    /// <summary>把 sysfs 的 PCI vendor id（"0x1002"）映射为厂商；无法解析或未知厂商返回 null。</summary>
    private static GpuVendor? TryMapVendorId(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var trimmed = text.Trim();
        if (!trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed switch
        {
            _ when trimmed.Equals("0x10de", StringComparison.OrdinalIgnoreCase) => GpuVendor.Nvidia,
            _ when trimmed.Equals("0x1002", StringComparison.OrdinalIgnoreCase) => GpuVendor.Amd,
            _ when trimmed.Equals("0x8086", StringComparison.OrdinalIgnoreCase) => GpuVendor.Intel,
            _ => null,
        };
    }

    /// <summary>读取 vendor 文件内容；IO 失败返回 null（当作探测不到，不让单张坏卡片拖垮整个探测）。</summary>
    private static string? ReadVendorId(string vendorFile)
    {
        try
        {
            return File.ReadAllText(vendorFile);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
