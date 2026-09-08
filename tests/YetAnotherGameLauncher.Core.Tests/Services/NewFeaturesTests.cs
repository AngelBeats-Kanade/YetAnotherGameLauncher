using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

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

        var (command, env) = CompatTools.BuildProtonLaunch("dw-proton", _home.Path);

        Assert.Contains("dw-proton", command);
        Assert.Contains("proton\" run {exe}", command);
        Assert.Equal("{installDir}/compatdata", env["STEAM_COMPAT_DATA_PATH"]);
    }

    [Fact]
    public void BuildProtonLaunch_MissingVersion_FallsBackToExpectedPath()
    {
        var (command, env) = CompatTools.BuildProtonLaunch("dw-proton", _home.Path);

        Assert.Contains(".steam", command);
        Assert.True(env.ContainsKey("STEAM_COMPAT_CLIENT_INSTALL_PATH"));
    }
}

public class AutostartContentTests
{
    [Fact]
    public void BuildDesktopContent_QuotesExecPath()
    {
        var content = AutostartService.BuildDesktopContent("/opt/yagl/YetAnotherGameLauncher");

        Assert.Contains("[Desktop Entry]", content);
        Assert.Contains("Exec=\"/opt/yagl/YetAnotherGameLauncher\"", content);
        Assert.Contains("Type=Application", content);
    }

    [Fact]
    public void DesktopFilePath_UnderXdgAutostart()
    {
        var path = AutostartService.DesktopFilePath("/home/user");

        Assert.StartsWith("/home/user", path);
        Assert.EndsWith(System.IO.Path.Combine(".config", "autostart", "yetanothergamelauncher.desktop"), path);
    }
}

public class KuroLauncherBackgroundTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void FindLatestFrame_ProbesSiblingCacheDirectory()
    {
        // 游戏目录：temp/Wuthering Waves/Wuthering Waves Game；缓存：temp/Wuthering Waves/kr_game_cache
        var installDir = _home.FilePath("Wuthering Waves", "Wuthering Waves Game");
        Directory.CreateDirectory(installDir);
        var frame = _home.FilePath("Wuthering Waves", "kr_game_cache", "animate_bg", "h1", "home_7.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(frame)!);
        File.WriteAllText(frame, "x");

        Assert.Equal(frame, KuroLauncherBackground.FindLatestFrame(installDir, ["Z:\\"]));
    }

    [Fact]
    public void FindLatestFrame_ReturnsNewestFrame()
    {
        var installDir = _home.FilePath("Wuthering Waves", "Wuthering Waves Game");
        Directory.CreateDirectory(installDir);
        foreach (var n in new[] { 1, 2, 9, 10 })
        {
            var f = _home.FilePath("kr_game_cache", "animate_bg", "h1", $"home_{n}.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, "x");
        }

        // 帧序号按数值比较：home_10 是末帧而非 home_9（文件名非零填充）
        Assert.EndsWith($"home_10{System.IO.Path.GetExtension(".jpg")}", KuroLauncherBackground.FindLatestFrame(installDir, ["Z:\\"]));
    }

    [Fact]
    public void FindLatestFrame_None_ReturnsNull()
    {
        var installDir = _home.FilePath("Wuthering Waves", "Wuthering Waves Game");
        Directory.CreateDirectory(installDir);

        Assert.Null(KuroLauncherBackground.FindLatestFrame(installDir, ["Z:\\"]));
    }

    [Fact]
    public void FindLatestSwitchConfig_PicksNewestTimestampedResponse()
    {
        // 伪造 Chromium 缓存块文件：两份历史配置（旧投放与较新投放），必须取 _t 较大的一份
        var cacheDir = _home.FilePath("KRLauncher", "G152", "C10003", "KRWebViewUserData", "EBWebView", "Default", "Cache_Data");
        Directory.CreateDirectory(cacheDir);
        var noise = new string(' ', 64); // simple-cache 二进制噪声
        File.WriteAllText(
            Path.Combine(cacheDir, "data_1"),
            $"{noise}…/switch.json?_t=1742582942{noise}" +
            """{"functionSwitch":1,"backgroundFile":"https://cdn.example.com/old.mp4","backgroundFileType":2,"firstFrameImage":"https://cdn.example.com/old.webp"}""" + noise);
        File.WriteAllText(
            Path.Combine(cacheDir, "data_2"),
            $"{noise}…/switch.json?_t=1749488617{noise}" +
            """{"functionSwitch":1,"backgroundFile":"https://cdn.example.com/new.mp4","backgroundFileType":2,"firstFrameImage":"https://cdn.example.com/new.webp"}""" + noise);

        var config = KuroLauncherBackground.FindLatestSwitchConfig(
            _home.FilePath("KRLauncher"), _home.FilePath("persist.json"));

        Assert.Equal("https://cdn.example.com/new.mp4", config!.BackgroundFile);
        Assert.Equal("https://cdn.example.com/new.webp", config.FirstFrameImage);
        // 扫描结果已持久化（Chromium 缓存淘汰后的兜底）
        Assert.True(File.Exists(_home.FilePath("persist.json")));
    }

    [Fact]
    public void FindLatestSwitchConfig_CacheGone_FallsBackToPersisted()
    {
        var cacheDir = _home.FilePath("KRLauncher", "Cache_Data");
        Directory.CreateDirectory(cacheDir);
        var persistPath = _home.FilePath("persist.json");
        KuroLauncherBackground.PersistSwitchConfig(
            new KuroSwitchConfig("https://cdn.example.com/kept.mp4", "https://cdn.example.com/kept.webp", null),
            persistPath);

        // 缓存目录空（被 Chromium LRU 淘汰）：回退上次持久化的配置
        var config = KuroLauncherBackground.FindLatestSwitchConfig(_home.FilePath("KRLauncher"), persistPath);

        Assert.Equal("https://cdn.example.com/kept.mp4", config!.BackgroundFile);
    }

    [Fact]
    public void FindLatestSwitchConfig_NothingAvailable_ReturnsNull()
    {
        Directory.CreateDirectory(_home.FilePath("KRLauncher"));

        Assert.Null(KuroLauncherBackground.FindLatestSwitchConfig(
            _home.FilePath("KRLauncher"), _home.FilePath("missing.json")));
    }
}
