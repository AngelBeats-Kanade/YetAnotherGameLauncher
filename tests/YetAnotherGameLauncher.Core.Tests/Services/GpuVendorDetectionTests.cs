using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>
/// GPU 厂商探测：NVIDIA 经 /proc 闭源驱动探测（既有语义），AMD/Intel 经 /sys/class/drm 的
/// PCI vendor 探测；路径全部可注入，Windows 测试机上即可覆盖 Linux 分支。
/// </summary>
public class GpuVendorDetectionTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public void GpuVendors_AmdCardViaSysfs_ReturnsAmd()
    {
        WriteVendor("card0", "0x1002");
        var platform = NewPlatform();

        Assert.Contains(GpuVendor.Amd, platform.GpuVendors);
    }

    [Fact]
    public void GpuVendors_IntelCardViaSysfs_ReturnsIntel()
    {
        WriteVendor("card0", "0x8086");
        var platform = NewPlatform();

        Assert.Contains(GpuVendor.Intel, platform.GpuVendors);
    }

    [Fact]
    public void GpuVendors_NvidiaCardViaSysfs_ReturnsNvidia()
    {
        WriteVendor("card0", "0x10de");
        var platform = NewPlatform();

        Assert.Contains(GpuVendor.Nvidia, platform.GpuVendors);
    }

    [Fact]
    public void GpuVendors_HybridLaptop_MergesProcNvidiaWithSysfsAmd()
    {
        // 典型双卡机器：/proc 只反映闭源驱动，sysfs 反映所有 PCI 显示设备
        WriteVendor("card0", "0x1002");
        WriteVendor("card1", "0x10de");
        var probe = WriteNvidiaProbe();

        var platform = NewPlatform(procNvidiaVersionPath: probe);

        Assert.Equal(
            [GpuVendor.Nvidia, GpuVendor.Amd],
            platform.GpuVendors.OrderBy(v => v).ToArray());
    }

    [Fact]
    public void GpuVendors_DuplicateVendorAcrossCards_Deduplicates()
    {
        WriteVendor("card0", "0x8086");
        WriteVendor("card1", "0x8086");

        Assert.Single(NewPlatform().GpuVendors);
    }

    [Fact]
    public void GpuVendors_MalformedVendorFile_IsIgnored()
    {
        WriteVendor("card0", "not-a-vendor");
        WriteVendor("card1", "0x1002");

        Assert.Equal([GpuVendor.Amd], NewPlatform().GpuVendors);
    }

    [Fact]
    public void GpuVendors_UnknownVendorId_IsIgnored()
    {
        WriteVendor("card0", "0x1a03");

        Assert.Empty(NewPlatform().GpuVendors);
    }

    [Fact]
    public void GpuVendors_NoCards_ReturnsEmpty()
    {
        Assert.Empty(NewPlatform().GpuVendors);
    }

    [Fact]
    public void GpuVendors_NoNvidiaProcFile_SysfsAlone()
    {
        WriteVendor("card0", "0x1002");
        var platform = NewPlatform(
            procNvidiaVersionPath: _tempDir.FilePath("proc", "nvidia", "version"));

        Assert.Equal([GpuVendor.Amd], platform.GpuVendors);
    }

    private LinuxPlatformInfo NewPlatform(string? procNvidiaVersionPath = null) => new(
        procNvidiaVersionPath: procNvidiaVersionPath ?? _tempDir.FilePath("proc", "nvidia", "version"),
        sysfsDrmRoot: _tempDir.FilePath("sys", "class", "drm"));

    private void WriteVendor(string card, string vendor)
    {
        var path = _tempDir.FilePath("sys", "class", "drm", card, "device", "vendor");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, vendor + "\n");
    }

    private string WriteNvidiaProbe()
    {
        var probe = _tempDir.FilePath("proc", "nvidia", "version");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(probe)!);
        File.WriteAllText(probe, "NVIDIA UNIX Open Kernel Module");
        return probe;
    }
}
