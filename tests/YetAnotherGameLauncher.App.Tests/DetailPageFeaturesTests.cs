using System.Net;
using Avalonia.Controls;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>背景图加载：本地路径 / URL / 失败回退 / 缓存。</summary>
public class BackgroundImageServiceTests
{
    /// <summary>1×1 PNG。</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [Fact]
    public async Task LoadAsync_LocalFile_ReturnsImage()
    {
        // Bitmap 解码依赖 Avalonia 引擎，需在 headless 会话内执行
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            using var dir = new TempDir();
            var path = dir.FilePath("bg.png");
            await File.WriteAllBytesAsync(path, Png);
            var service = new BackgroundImageService(new HttpClient(new StubHttpHandler()));

            var image = await service.LoadAsync(path);

            Assert.NotNull(image);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_MissingLocalFile_ReturnsNull()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            using var dir = new TempDir();
            var service = new BackgroundImageService(new HttpClient(new StubHttpHandler()));

            var image = await service.LoadAsync(dir.FilePath("nope.png"));

            Assert.Null(image);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_HttpSource_ReturnsImageAndCaches()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            var handler = new StubHttpHandler();
            handler.Map("https://cdn.example/bg.png", Png);
            var service = new BackgroundImageService(new HttpClient(handler));

            var first = await service.LoadAsync("https://cdn.example/bg.png");
            var second = await service.LoadAsync("https://cdn.example/bg.png");

            Assert.NotNull(first);
            Assert.Same(first, second); // 会话内缓存
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_HttpFailure_ReturnsNull()
    {
        await HeadlessSession.Instance.Dispatch(async () =>
        {
            var handler = new StubHttpHandler { FailFirstN = 3 };
            var service = new BackgroundImageService(new HttpClient(handler));

            var image = await service.LoadAsync("https://cdn.example/missing.png");

            Assert.Null(image);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_EmptySource_ReturnsNull()
    {
        var service = new BackgroundImageService(new HttpClient(new StubHttpHandler()));

        Assert.Null(await service.LoadAsync(""));
        Assert.Null(await service.LoadAsync(null));
    }
}

/// <summary>启动设置卡：编辑保存回 games.json 与校验提示。</summary>
[Collection("sequential")]
public class LaunchSettingsTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public LaunchSettingsTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.TempDir.Dispose();

    [Fact]
    public async Task Save_UpdatesGameLaunchAndConfigFile()
    {
        await _ctx.Vm.InitializeAsync();
        var settings = _ctx.Vm.Games[0].LaunchSettings;

        settings.CommandTemplate = "wine {exe}";
        settings.WorkingDirectory = "{installDir}";
        settings.EnvironmentText = "WINEPREFIX=/tmp/pfx\nLANG=zh_CN.UTF-8";
        await settings.SaveCommand.ExecuteAsync(null);

        Assert.False(settings.SaveFailed);
        Assert.Equal("启动设置已保存", settings.SaveMessage);

        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        var launch = reloader.Catalog!.Games[0].Launch;
        Assert.Equal("wine {exe}", launch.CommandTemplate);
        Assert.Equal("/tmp/pfx", launch.Environment["WINEPREFIX"]);
        Assert.Equal("zh_CN.UTF-8", launch.Environment["LANG"]);
    }

    [Fact]
    public async Task Save_InvalidEnvironmentLine_ShowsErrorWithoutSaving()
    {
        await _ctx.Vm.InitializeAsync();
        var settings = _ctx.Vm.Games[0].LaunchSettings;
        settings.EnvironmentText = "NOT-A-PAIR";

        await settings.SaveCommand.ExecuteAsync(null);

        Assert.True(settings.SaveFailed);
        Assert.Contains("NOT-A-PAIR", settings.SaveMessage);

        var reloader = new YetAnotherGameLauncher.Core.Services.GameCatalogService(_ctx.ConfigPath);
        await reloader.LoadAsync();
        Assert.Empty(reloader.Catalog!.Games[0].Launch.Environment);
    }

    [Fact]
    public async Task Save_EmptyCommandTemplate_ShowsError()
    {
        await _ctx.Vm.InitializeAsync();
        var settings = _ctx.Vm.Games[0].LaunchSettings;
        settings.CommandTemplate = "  ";

        await settings.SaveCommand.ExecuteAsync(null);

        Assert.True(settings.SaveFailed);
        Assert.Equal("命令模板不能为空", settings.SaveMessage);
    }

    [Fact]
    public async Task InitialValues_ComeFromGameLaunch()
    {
        await _ctx.Vm.InitializeAsync();

        var settings = _ctx.Vm.Games[0].LaunchSettings;
        Assert.Equal("{exe}", settings.CommandTemplate);
        Assert.Equal("{installDir}", settings.WorkingDirectory);
        Assert.Equal("", settings.EnvironmentText.Trim());
    }
}
