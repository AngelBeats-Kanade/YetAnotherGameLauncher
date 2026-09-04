using System.Text.Json;
using System.Text.Json.Serialization;
using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>配置校验失败异常，Errors 包含全部错误（中文化，可直接展示给用户）。</summary>
public sealed class GameCatalogValidationException(IReadOnlyList<string> errors)
    : Exception($"游戏配置无效（{errors.Count} 处错误）：{string.Join("；", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>负责 games.json 的加载、校验与保存。文件缺失时不自动创建，由上层决定如何引导用户。</summary>
public sealed class GameCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _configFilePath;

    public GameCatalogService() : this(AppPaths.ConfigFilePath)
    {
    }

    public GameCatalogService(string configFilePath) => _configFilePath = configFilePath;

    /// <summary>当前加载的目录；Load 失败时为 null。</summary>
    public GameCatalog? Catalog { get; set; }

    public string ConfigFilePath => _configFilePath;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configFilePath))
        {
            throw new FileNotFoundException("游戏配置文件不存在", _configFilePath);
        }

        var json = await File.ReadAllTextAsync(_configFilePath, cancellationToken);
        Catalog = Parse(json);
    }

    /// <summary>原子保存：先写临时文件再替换，避免写入中途崩溃损坏配置。</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (Catalog is null)
        {
            throw new InvalidOperationException("尚未加载或设置任何配置，无法保存。");
        }

        var directory = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _configFilePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, Serialize(Catalog), cancellationToken);
        File.Move(tempPath, _configFilePath, overwrite: true);
    }

    /// <summary>反序列化并校验。任何 JSON 或语义错误都汇总为 GameCatalogValidationException。</summary>
    public static GameCatalog Parse(string json)
    {
        GameCatalog catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<GameCatalog>(json, JsonOptions)
                      ?? throw new GameCatalogValidationException(["配置文件内容为空。"]);
        }
        catch (JsonException ex)
        {
            throw new GameCatalogValidationException([$"配置文件 JSON 格式错误：{ex.Message}"]);
        }

        var errors = Validate(catalog);
        return errors.Count > 0 ? throw new GameCatalogValidationException(errors) : catalog;
    }

    public static string Serialize(GameCatalog catalog) =>
        JsonSerializer.Serialize(catalog, JsonOptions);

    /// <summary>对目录做完整语义校验，返回全部错误；无错误时返回空列表。</summary>
    public static IReadOnlyList<string> Validate(GameCatalog catalog)
    {
        var errors = new List<string>();
        var settings = catalog.Settings;

        if (string.IsNullOrWhiteSpace(settings.InstallRoot))
        {
            errors.Add("settings.installRoot 不能为空。");
        }

        if (settings.MaxParallelDownloads is < 1 or > 64)
        {
            errors.Add("settings.maxParallelDownloads 必须在 1-64 之间。");
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

        if (string.IsNullOrWhiteSpace(game.Id))
        {
            errors.Add($"{field}.id 不能为空。");
        }
        else if (!IsValidId(game.Id))
        {
            errors.Add($"{field}.id 含有非法字符，只允许字母、数字、'-'、'_'、'.'。");
        }
        else if (!seenGameIds.Add(game.Id))
        {
            errors.Add($"{field}.id 重复：{game.Id}。");
        }

        if (string.IsNullOrWhiteSpace(game.DisplayName))
        {
            errors.Add($"{field}.displayName 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(game.Channel))
        {
            errors.Add($"{field}.channel 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(game.InstallDir))
        {
            errors.Add($"{field}.installDir 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(game.Executable))
        {
            errors.Add($"{field}.executable 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(game.Launch.CommandTemplate))
        {
            errors.Add($"{field}.launch.commandTemplate 不能为空。");
        }

        if (game.Servers.Count == 0)
        {
            errors.Add($"{field}.servers 至少需要配置一个服务器。");
            return;
        }

        var seenServerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var s = 0; s < game.Servers.Count; s++)
        {
            var server = game.Servers[s];
            var serverField = $"{field}.servers[{s}]";

            if (string.IsNullOrWhiteSpace(server.Id))
            {
                errors.Add($"{serverField}.id 不能为空。");
            }
            else if (!seenServerIds.Add(server.Id))
            {
                errors.Add($"{serverField}.id 重复：{server.Id}。");
            }

            if (string.IsNullOrWhiteSpace(server.Name))
            {
                errors.Add($"{serverField}.name 不能为空。");
            }
        }
    }

    private static bool IsValidId(string id)
    {
        foreach (var ch in id)
        {
            var valid = char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    private static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
