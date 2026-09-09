using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>Linux 平台环境：NVIDIA 探测路径可注入（Windows 测试机上即可覆盖 Linux 逻辑分支）。</summary>
public class LinuxPlatformInfoTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public void IsNvidiaGpuPresent_ProbeFileExists_ReturnsTrue()
    {
        var probe = _tempDir.FilePath("proc", "nvidia", "version");
        Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
        File.WriteAllText(probe, "NVIDIA UNIX Open Kernel Module");

        var platform = new LinuxPlatformInfo(procNvidiaVersionPath: probe);

        Assert.True(platform.IsNvidiaGpuPresent);
    }

    [Fact]
    public void IsNvidiaGpuPresent_ProbeFileMissing_ReturnsFalse()
    {
        var platform = new LinuxPlatformInfo(
            procNvidiaVersionPath: _tempDir.FilePath("proc", "nvidia", "version"));

        Assert.False(platform.IsNvidiaGpuPresent);
    }

    [Fact]
    public void IsLinux_AlwaysTrue()
    {
        Assert.True(new LinuxPlatformInfo().IsLinux);
    }
}
