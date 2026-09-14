using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class SpeedLimiterTests
{
    [Fact]
    public void Acquire_Unlimited_ReturnsZero()
    {
        var limiter = new SpeedLimiter(new ManualTimeProvider());
        limiter.BytesPerSecond = 0;

        Assert.Equal(TimeSpan.Zero, limiter.Acquire(1024));
        Assert.Equal(TimeSpan.Zero, limiter.Acquire(1024));
    }

    [Fact]
    public void Acquire_QueuesBeyondBudget()
    {
        var time = new ManualTimeProvider();
        var limiter = new SpeedLimiter(time) { BytesPerSecond = 1000 };

        // 第一批立即发放
        Assert.Equal(TimeSpan.Zero, limiter.Acquire(600));

        // 第二批超出本秒预算：需等待到下一个周期
        var wait = limiter.Acquire(600);
        Assert.True(wait > TimeSpan.FromSeconds(0.5), $"wait={wait}");
        Assert.True(wait <= TimeSpan.FromSeconds(1.2), $"wait={wait}");

        // 虚拟时钟推进过排队窗口后，第三批不再等待
        time.Advance(wait + TimeSpan.FromMilliseconds(600));
        Assert.Equal(TimeSpan.Zero, limiter.Acquire(300));
    }

    [Fact]
    public void SetZero_ResetQueue()
    {
        var time = new ManualTimeProvider();
        var limiter = new SpeedLimiter(time) { BytesPerSecond = 1000 };
        limiter.Acquire(1000);

        limiter.BytesPerSecond = 0;
        Assert.Equal(TimeSpan.Zero, limiter.Acquire(4096));
    }
}

public class CompatToolsTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void FindProtonVersions_ScansKnownDirs_PutsDefaultFirst()
    {
        Directory.CreateDirectory(_home.FilePath(".steam/steam/compatibilitytools.d/GE-Proton9"));
        Directory.CreateDirectory(_home.FilePath(".steam/steam/compatibilitytools.d/dw-proton"));
        Directory.CreateDirectory(_home.FilePath(".local/share/Steam/steamapps/common/SomeGame")); // 非 Proton，忽略

        var versions = CompatTools.FindProtonVersions(_home.Path);

        Assert.Equal("dw-proton", versions[0]); // 默认推荐置顶
        Assert.Contains("GE-Proton9", versions);
        Assert.DoesNotContain("SomeGame", versions);
    }

    [Fact]
    public void BuildProtonLaunch_GeneratesTemplateAndEnv()
    {
        Directory.CreateDirectory(_home.FilePath(".steam/steam/compatibilitytools.d/dw-proton"));

        // 数据根显式注入：缺省取 AppPaths.DataHomeDirectory，不随 home 推导
        var launch = CompatTools.BuildProtonLaunch(
            "wuthering-waves", "dw-proton", _home.Path, dataHome: _home.FilePath(".local", "share"));

        Assert.Contains("dw-proton", launch.CommandTemplate);
        Assert.Contains("proton\" run {exe}", launch.CommandTemplate);
        Assert.Equal(
            _home.FilePath(".local", "share", "yagl", "prefixes", "wuthering-waves"),
            launch.Environment["STEAM_COMPAT_DATA_PATH"]);
    }

    [Fact]
    public void BuildProtonLaunch_MissingVersion_FallsBackToExpectedPath()
    {
        var launch = CompatTools.BuildProtonLaunch("wuthering-waves", "dw-proton", _home.Path);

        Assert.Contains(".steam", launch.CommandTemplate);
        Assert.True(launch.Environment.ContainsKey("STEAM_COMPAT_CLIENT_INSTALL_PATH"));
    }
}

public class AutostartContentTests
{
    [Fact]
    public void BuildDesktopContent_QuotesExecPath()
    {
        var content = LinuxAutostartService.BuildDesktopContent("/opt/yagl/YetAnotherGameLauncher");

        Assert.Contains("[Desktop Entry]", content);
        Assert.Contains("Exec=\"/opt/yagl/YetAnotherGameLauncher\"", content);
        Assert.Contains("Type=Application", content);
    }

    [Fact]
    public void DesktopFilePath_UnderXdgAutostart()
    {
        var path = LinuxAutostartService.DesktopFilePath("/home/user");

        Assert.StartsWith("/home/user", path);
        Assert.EndsWith(Path.Combine(".config", "autostart", "yetanothergamelauncher.desktop"), path);
    }
}
