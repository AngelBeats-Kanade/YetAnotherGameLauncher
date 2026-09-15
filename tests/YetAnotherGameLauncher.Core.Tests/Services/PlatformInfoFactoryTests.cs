using Xunit;
using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class PlatformInfoFactoryTests
{
    [Fact]
    public void Create_MatchesCurrentOs()
    {
        // 全仓唯一的 IPlatformInfo 平台分支：落地类型必须与当前 OS 一致
        var platform = PlatformInfoFactory.Create();
        Assert.Equal(OperatingSystem.IsLinux(), platform is LinuxPlatformInfo);
    }
}
