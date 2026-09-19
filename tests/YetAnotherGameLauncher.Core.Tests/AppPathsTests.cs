using Xunit;

namespace YetAnotherGameLauncher.Core.Tests;

/// <summary>
/// 平台数据路径解析（2026-09-19 Windows CI 腿补测）：Windows 分支取 %LOCALAPPDATA%。
/// Linux 的 XDG 分支由 AppPaths 的 Linux 用例/使用方覆盖；静态属性初始化一次，按平台断言。
/// </summary>
public class AppPathsTests
{
    [Fact]
    public void DataHome_ResolvedPerPlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows 分支：Linux 走 XDG_DATA_HOME 解析（由 CompatTools 前缀测试覆盖）");
        }

        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppPaths.DataHomeDirectory);
        Assert.EndsWith("yagl", AppPaths.DataDirectory, StringComparison.Ordinal);
        Assert.EndsWith("yagl", AppPaths.ConfigDirectory, StringComparison.Ordinal);
    }
}
