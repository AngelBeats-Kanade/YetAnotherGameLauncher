using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class GameCatalogServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly string _configPath;

    public GameCatalogServiceTests() => _configPath = _tempDir.FilePath("games.json");

    public void Dispose() => _tempDir.Dispose();

    private const string ValidJson = """
        {
          "settings": { "installRoot": "~/Games", "theme": "Light" },
          "games": [
            {
              "id": "test-game",
              "displayName": "测试游戏",
              "channel": "kuro",
              "installDir": "TestGame",
              "executable": "game.exe",
              "servers": [ { "id": "s1", "name": "服务器1" } ]
            }
          ]
        }
        """;

    [Fact]
    public async Task LoadAsync_MissingFile_ThrowsFileNotFoundException()
    {
        var service = new GameCatalogService(_configPath);

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_ValidFile_PopulatesCatalog()
    {
        await File.WriteAllTextAsync(_configPath, ValidJson, TestContext.Current.CancellationToken);
        var service = new GameCatalogService(_configPath);

        await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(service.Catalog);
        var game = Assert.Single(service.Catalog.Games);
        Assert.Equal("test-game", game.Id);
        Assert.Equal(ThemeMode.Light, service.Catalog.Settings.Theme);
    }

    [Fact]
    public async Task LoadAsync_InvalidContent_ThrowsValidationExceptionWithErrors()
    {
        await File.WriteAllTextAsync(_configPath, """{ "games": [ { "id": "" } ] }""", TestContext.Current.CancellationToken);
        var service = new GameCatalogService(_configPath);

        var ex = await Assert.ThrowsAsync<GameCatalogValidationException>(() => service.LoadAsync(TestContext.Current.CancellationToken));

        Assert.NotEmpty(ex.Errors);
        Assert.Null(service.Catalog);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var service = new GameCatalogService(_configPath)
        {
            Catalog = GameCatalogService.Parse(ValidJson),
        };

        await service.SaveAsync(TestContext.Current.CancellationToken);

        var reloader = new GameCatalogService(_configPath);
        await reloader.LoadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(reloader.Catalog);
        Assert.Equal("test-game", Assert.Single(reloader.Catalog.Games).Id);
    }

    [Fact]
    public async Task SaveAsync_WritesConfigFile()
    {
        var service = new GameCatalogService(_configPath)
        {
            Catalog = GameCatalogService.Parse(ValidJson),
        };

        await service.SaveAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(_configPath));
    }

    [Fact]
    public async Task SaveAsync_WithoutCatalog_ThrowsInvalidOperationException()
    {
        var service = new GameCatalogService(_configPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GetConfigFilePath_EndsWithYaglGamesJson()
    {
        var path = AppPaths.GetConfigFilePath();

        Assert.EndsWith(Path.Combine("yagl", "games.json"), path, StringComparison.Ordinal);
    }

    [Fact]
    public void GetConfigDirectory_IsUnderUserConfigRoot()
    {
        var dir = AppPaths.ConfigDirectory;
        var expectedRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.StartsWith(expectedRoot, dir, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- CreateDefaultFileAsync：首次运行自动生成默认配置 ----------

    private const string TemplateJson = """
        {
          "settings": { "installRoot": "~/Games", "theme": "Dark" },
          "games": [
            {
              "id": "template-game",
              "displayName": "模板游戏",
              "channel": "kuro",
              "installDir": "Template",
              "executable": "game.exe",
              "servers": [ { "id": "s1", "name": "S1" } ]
            }
          ]
        }
        """;

    [Fact]
    public async Task CreateDefaultFileAsync_MissingFile_WritesValidMinimalDefault()
    {
        var service = new GameCatalogService(_configPath);

        var created = await service.CreateDefaultFileAsync();

        Assert.True(created);
        Assert.True(File.Exists(_configPath));
        // 生成的文件必须能通过解析与全量校验
        var reloader = new GameCatalogService(_configPath);
        await reloader.LoadAsync();
        Assert.NotNull(reloader.Catalog);
        Assert.Empty(reloader.Catalog.Games);
        Assert.Equal("~/Games", reloader.Catalog.Settings.InstallRoot);
        Assert.Equal(8, reloader.Catalog.Settings.MaxParallelDownloads);
    }

    [Fact]
    public async Task CreateDefaultFileAsync_ExistingFile_IsLeftUntouched()
    {
        await File.WriteAllTextAsync(_configPath, ValidJson);
        var service = new GameCatalogService(_configPath);

        var created = await service.CreateDefaultFileAsync();

        Assert.False(created);
        // 原文件内容未被覆盖（ValidJson 的主题是 Light）
        var reloader = new GameCatalogService(_configPath);
        await reloader.LoadAsync();
        Assert.NotNull(reloader.Catalog);
        Assert.Equal(ThemeMode.Light, reloader.Catalog.Settings.Theme);
    }

    [Fact]
    public async Task CreateDefaultFileAsync_WithValidTemplate_WritesTemplateContent()
    {
        var service = new GameCatalogService(_configPath);

        var created = await service.CreateDefaultFileAsync(TemplateJson);

        Assert.True(created);
        var reloader = new GameCatalogService(_configPath);
        await reloader.LoadAsync();
        Assert.NotNull(reloader.Catalog);
        var game = Assert.Single(reloader.Catalog.Games);
        Assert.Equal("template-game", game.Id);
    }

    [Fact]
    public async Task CreateDefaultFileAsync_InvalidTemplate_FallsBackToMinimalDefault()
    {
        // 缺 installRoot 的模板无法通过校验 → 回退到内置最小默认
        var invalidTemplate = """{ "games": [] }""";
        var service = new GameCatalogService(_configPath);

        var created = await service.CreateDefaultFileAsync(invalidTemplate);

        Assert.True(created);
        var reloader = new GameCatalogService(_configPath);
        await reloader.LoadAsync();
        Assert.NotNull(reloader.Catalog);
        Assert.Empty(reloader.Catalog.Games);
        Assert.Equal("~/Games", reloader.Catalog.Settings.InstallRoot);
    }

    [Fact]
    public async Task CreateDefaultFileAsync_CreatesMissingParentDirectories()
    {
        var nestedPath = _tempDir.FilePath("a", "b", "games.json");
        var service = new GameCatalogService(nestedPath);

        var created = await service.CreateDefaultFileAsync();

        Assert.True(created);
        Assert.True(File.Exists(nestedPath));
        var reloader = new GameCatalogService(nestedPath);
        await reloader.LoadAsync();
        Assert.NotNull(reloader.Catalog);
    }
}
