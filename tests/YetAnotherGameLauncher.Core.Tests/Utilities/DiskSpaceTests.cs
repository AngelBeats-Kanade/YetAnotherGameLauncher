using Xunit;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Tests.Utilities;

/// <summary>
/// 空闲空间探测的最长挂载点匹配（2026-10-18 空间预检配套）：驱动器根不能直接喂给 DriveInfo，
/// 必须按挂载点前缀匹配，且前缀落在目录边界上、Windows 盘符不区分大小写。
/// </summary>
public class DiskSpaceTests
{
    [Fact]
    public void Probe_PicksLongestMountPrefix()
    {
        var drives = new List<(string, long)> { ("/", 100), ("/home", 50), ("/home/user", 20) };

        Assert.Equal(20, DiskSpace.ProbeAgainstMountedDrives("/home/user/games/ef", drives, windows: false));
        Assert.Equal(50, DiskSpace.ProbeAgainstMountedDrives("/home/other/games", drives, windows: false));
        Assert.Equal(100, DiskSpace.ProbeAgainstMountedDrives("/mnt/data/games", drives, windows: false));
        Assert.Equal(20, DiskSpace.ProbeAgainstMountedDrives("/home/user", drives, windows: false)); // 恰好等于挂载点
    }

    [Fact]
    public void Probe_PrefixMustEndOnDirectoryBoundary()
    {
        // /home 不得吞掉 /homex（词法前缀不是挂载点前缀）
        var drives = new List<(string, long)> { ("/", 100), ("/home", 50) };

        Assert.Equal(100, DiskSpace.ProbeAgainstMountedDrives("/homex/games", drives, windows: false));
    }

    [Fact]
    public void Probe_NoMatchingMount_ReturnsNull()
    {
        Assert.Null(DiskSpace.ProbeAgainstMountedDrives(
            "/mnt/games", new List<(string, long)> { ("/home", 50) }, windows: false));
        Assert.Null(DiskSpace.ProbeAgainstMountedDrives("/x", [], windows: false));
    }

    [Fact]
    public void Probe_WindowsDriveRoots_MatchCaseInsensitively()
    {
        var drives = new List<(string, long)> { (@"C:\", 100), (@"D:\", 200) };

        Assert.Equal(200, DiskSpace.ProbeAgainstMountedDrives(@"D:\Games\x", drives, windows: true));
        Assert.Equal(200, DiskSpace.ProbeAgainstMountedDrives(@"d:/games/x", drives, windows: true));
        Assert.Equal(200, DiskSpace.ProbeAgainstMountedDrives(@"D:\", drives, windows: true));
    }

    [Fact]
    public void Probe_TrailingSeparatorOnMount_StillMatches()
    {
        var drives = new List<(string, long)> { ("/", 100), ("/mnt/data/", 42) };

        Assert.Equal(42, DiskSpace.ProbeAgainstMountedDrives("/mnt/data/games", drives, windows: false));
    }

    [Fact]
    public void TryGetAvailableFreeBytes_RealVolumeOnThisMachine_OrUnknown()
    {
        // 真机面：临时目录所在卷应能探出空闲值（探测不出时返回 null 也是合法结果——调用方跳过预检）
        var free = DiskSpace.TryGetAvailableFreeBytes(Path.GetTempPath());

        Assert.True(free is null or > 0, $"unexpected free space value: {free}");
    }
}
