using System.Text.Json;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>配置校验失败异常，Errors 包含全部错误（中文化，可直接展示给用户）。</summary>
public sealed class GameCatalogValidationException(IReadOnlyList<string> errors)
    : Exception($"Invalid game config ({errors.Count} errors): {string.Join("; ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>负责 games.json 的加载、校验与保存。文件缺失时不自动创建，由上层决定如何引导用户。</summary>
public sealed class GameCatalogService
{
    private readonly string _configFilePath;

    public GameCatalogService() : this(AppPaths.GetConfigFilePath())
    {
    }

    public GameCatalogService(string configFilePath) => _configFilePath = configFilePath;

    /// <summary>当前加载的目录；Load 失败时为 null。</summary>
    public GameCatalog? Catalog { get; set; }

    /// <summary>games.json 的实际路径（默认在应用配置目录）。</summary>
    public string ConfigFilePath => _configFilePath;

    /// <summary>从磁盘读取并解析配置，结果写入 Catalog；文件缺失时抛 FileNotFoundException。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configFilePath))
        {
            throw new FileNotFoundException("Game config file not found", _configFilePath);
        }

        var json = await File.ReadAllTextAsync(_configFilePath, cancellationToken).ConfigureAwait(false);
        Catalog = Parse(json);
    }

    /// <summary>原子保存：先写临时文件再替换，避免写入中途崩溃损坏配置。</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (Catalog is null)
        {
            throw new InvalidOperationException("No config has been loaded; nothing to save.");
        }

        await FileUtilities.WriteAtomicAsync(_configFilePath, Serialize(Catalog), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 首次运行引导：配置文件不存在时在配置路径生成默认配置文件。
    /// </summary>
    /// <param name="templateJson">
    /// 可选的默认内容模板（如随应用分发的示例配置）。模板必须能通过完整校验，
    /// 否则回退到内置最小默认（空游戏列表 + 推荐设置）。
    /// </param>
    /// <returns>true 表示本次创建了文件；文件已存在时不做任何改动并返回 false。</returns>
    public async Task<bool> CreateDefaultFileAsync(string? templateJson = null, CancellationToken cancellationToken = default)
    {
        if (File.Exists(_configFilePath))
        {
            return false;
        }

        var content = templateJson;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                Parse(content);
            }
            catch (GameCatalogValidationException)
            {
                content = null;
            }
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            content = Serialize(DefaultCatalog());
        }

        await FileUtilities.WriteAtomicAsync(_configFilePath, content, cancellationToken).ConfigureAwait(false);

        Catalog = Parse(content);
        return true;
    }

    /// <summary>内置最小默认目录：不含任何游戏，仅提供可编辑的起点。</summary>
    private static GameCatalog DefaultCatalog() => new()
    {
        Settings = new AppSettings
        {
            InstallRoot = "~/Games",
            Theme = ThemeMode.System,
            Language = "zh-CN",
        },
        Games = [],
    };

    /// <summary>反序列化并校验。任何 JSON 或语义错误都汇总为 GameCatalogValidationException。</summary>
    public static GameCatalog Parse(string json)
    {
        GameCatalog catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<GameCatalog>(json, Json.Default)
                      ?? throw new GameCatalogValidationException(["Config file is empty."]);
        }
        catch (JsonException ex)
        {
            throw new GameCatalogValidationException([$"Config file contains invalid JSON: {ex.Message}"]);
        }

        var errors = Validate(catalog);
        return errors.Count > 0 ? throw new GameCatalogValidationException(errors) : catalog;
    }

    /// <summary>把目录序列化为配置文件 JSON（与 Parse 互逆）。</summary>
    public static string Serialize(GameCatalog catalog) =>
        JsonSerializer.Serialize(catalog, Json.Default);

    /// <summary>对目录做完整语义校验，返回全部错误；无错误时返回空列表。</summary>
    public static IReadOnlyList<string> Validate(GameCatalog catalog)
    {
        var errors = new List<string>();
        var settings = catalog.Settings;

        if (string.IsNullOrWhiteSpace(settings.InstallRoot))
        {
            errors.Add("settings.installRoot must not be empty.");
        }

        var seenGameIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < catalog.Games.Count; i++)
        {
            ValidateGame(catalog.Games[i], i, seenGameIds, errors);
        }

        return errors;
    }

    private static void ValidateGame(
        GameDefinition game, int index, HashSet<string> seenGameIds, List<string> errors)
    {
        var field = $"games[{index}]";

        // 必填字段统一校验（错误路径形如 "games[0].displayName"，一次报出全部错误）
        void Require(string? value, string path)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{path} must not be empty.");
            }
        }

        if (string.IsNullOrWhiteSpace(game.Id))
        {
            errors.Add($"{field}.id must not be empty.");
        }
        else if (!IsValidId(game.Id))
        {
            errors.Add($"{field}.id contains invalid characters; only letters, digits, '-', '_', '.' are allowed.");
        }
        else if (!seenGameIds.Add(game.Id))
        {
            errors.Add($"{field}.id is duplicated: {game.Id}.");
        }

        Require(game.DisplayName, $"{field}.displayName");
        Require(game.Channel, $"{field}.channel");
        Require(game.InstallDir, $"{field}.installDir");
        Require(game.Executable, $"{field}.executable");
        Require(game.Launch.CommandTemplate, $"{field}.launch.commandTemplate");

        if (game.Servers.Count == 0)
        {
            errors.Add($"{field}.servers requires at least one server.");
            return;
        }

        var seenServerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var s = 0; s < game.Servers.Count; s++)
        {
            var server = game.Servers[s];
            var serverField = $"{field}.servers[{s}]";

            if (string.IsNullOrWhiteSpace(server.Id))
            {
                errors.Add($"{serverField}.id must not be empty.");
            }
            else if (!seenServerIds.Add(server.Id))
            {
                errors.Add($"{serverField}.id is duplicated: {server.Id}.");
            }

            Require(server.Name, $"{serverField}.name");
        }
    }

    /// <summary>id 仅允许字母、数字与 -_.（用作安装子目录与状态文件键）。</summary>
    private static bool IsValidId(string id) =>
        id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
}
