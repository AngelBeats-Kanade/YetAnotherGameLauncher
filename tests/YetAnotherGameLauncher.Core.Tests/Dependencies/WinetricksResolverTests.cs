using Xunit;
using YetAnotherGameLauncher.Core.Dependencies;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Dependencies;

/// <summary>
/// winetricks 定位：Proton 自带优先、PATH 回退、都没有返回 null（安装器据此报 WinetricksMissing）。
/// 临时目录承载假 Proton 树，PATH 查找以委托注入避免依赖真机安装状态。
/// </summary>
public class WinetricksResolverTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>在 Proton 根下造自带 winetricks（Linux 补执行位；Windows 仅文件存在）。</summary>
    private string CreateBundledWinetricks(string protonRoot)
    {
        var path = Path.Combine(protonRoot, "protonfixes", "files", "bin", "winetricks");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    [Fact]
    public void Resolve_ProtonBundledPresent_PreferredOverPath()
    {
        var protonRoot = _temp.FilePath("dwproton-11.0-14");
        var bundled = CreateBundledWinetricks(protonRoot);
        var pathQueried = false;

        var resolution = WinetricksResolver.Resolve(protonRoot, _ =>
        {
            pathQueried = true;
            return "/usr/bin/winetricks";
        });

        Assert.NotNull(resolution);
        Assert.True(resolution.Bundled);
        Assert.Equal(bundled, resolution.ExecutablePath);
        Assert.False(pathQueried, "Proton 自带 winetricks 命中后不应再查 PATH");
    }

    [Fact]
    public void Resolve_BundledMissing_FallsBackToPath()
    {
        var protonRoot = _temp.FilePath("dwproton-no-winetricks");
        string? queriedName = null;

        var resolution = WinetricksResolver.Resolve(protonRoot, name =>
        {
            queriedName = name;
            return "/usr/bin/winetricks";
        });

        Assert.NotNull(resolution);
        Assert.False(resolution.Bundled);
        Assert.Equal("/usr/bin/winetricks", resolution.ExecutablePath);
        Assert.Equal("winetricks", queriedName);
    }

    [Fact]
    public void Resolve_NeitherPresent_ReturnsNull()
    {
        var resolution = WinetricksResolver.Resolve(_temp.FilePath("dwproton-empty"), _ => null);

        Assert.Null(resolution);
    }

    [Fact]
    public void Resolve_NullProtonRoot_SkipsBundledCandidate()
    {
        // 系统 wine / 自定义 prefix：没有 Proton 根，直接走 PATH
        var resolution = WinetricksResolver.Resolve(null, name => $"/custom/bin/{name}");

        Assert.NotNull(resolution);
        Assert.False(resolution.Bundled);
        Assert.Equal("/custom/bin/winetricks", resolution.ExecutablePath);
    }

    /// <summary>自带文件存在但没有执行位（损坏/半解包）：不得当作可用，回退系统 winetricks。</summary>
    [Fact]
    public void Resolve_BundledNotExecutable_FallsBackToPath()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("POSIX 执行位语义；Windows 上 IsExecutableFile 仅看文件存在");
            return;
        }

        var protonRoot = _temp.FilePath("dwproton-bad-winetricks");
        var path = Path.Combine(protonRoot, "protonfixes", "files", "bin", "winetricks");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var resolution = WinetricksResolver.Resolve(protonRoot, _ => "/usr/bin/winetricks");

        Assert.NotNull(resolution);
        Assert.False(resolution.Bundled);
        Assert.Equal("/usr/bin/winetricks", resolution.ExecutablePath);
    }
}
