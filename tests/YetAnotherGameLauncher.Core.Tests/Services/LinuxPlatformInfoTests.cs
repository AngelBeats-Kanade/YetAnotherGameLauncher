using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;

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

    [Fact]
    public void CreateXdgOpenInfo_PathWithSpaces_IsSingleEscapedArgument()
    {
        // F16（artifacts/bugs.md）：双参构造把 path 原样拼进 Arguments、按空白拆 argv——
        // 含空格的安装目录被 xdg-open 拆成多个参数（打开错误位置/无反应且无报错），
        // "-" 开头路径还会被当选项。ArgumentList 由 .NET 自动转义（官方文档："Strings
        // added to the list don't need to be previously escaped"），单参数语义在此钉住
        var psi = LinuxPlatformInfo.CreateXdgOpenInfo("/home/user/My Games/Weird Dir");

        Assert.Single(psi.ArgumentList);
        Assert.Equal("/home/user/My Games/Weird Dir", psi.ArgumentList[0]);
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void CreateXdgOpenInfo_UsesArgumentListChannel_Exclusively()
    {
        // "ArgumentList 与 Arguments 只能二选一"（官方文档 Remarks）：切到 ArgumentList
        // 通道后 Arguments 通道必须为空——两通道并用是未定义形态
        var psi = LinuxPlatformInfo.CreateXdgOpenInfo("-weird");

        Assert.Single(psi.ArgumentList);
        Assert.Equal("-weird", psi.ArgumentList[0]);
        Assert.Equal(string.Empty, psi.Arguments);
    }
}
