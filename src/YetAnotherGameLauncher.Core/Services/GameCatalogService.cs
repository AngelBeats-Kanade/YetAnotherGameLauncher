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

        // 写前校验（读严写严）：Catalog 是公开可变对象，任何保存路径漏守卫即可能写出
        // 下次启动拒载的配置——在唯一落盘口拦住（2026-09-20 三审修复）
        var errors = Validate(Catalog);
        if (errors.Count > 0)
        {
            throw new GameCatalogValidationException(errors);
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
    /// <param name="cancellationToken">取消令牌。</param>
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

        // 显式 null 的引用属性会被原样赋 null（未开反序列化 nullability 校验）——放行会让
        // Validate NRE，"手改坏配置→友好校验提示"的防线被绕过成无提示空壳启动（2026-09-20 三审修复）。
        // F14 嵌套延伸：null 同样能覆盖嵌套层的 = new() 初始化器（games 元素/launch/servers/
        // servers 元素/nameLocalized），防线与错误口径下探；environment/options 更深的字典袋
        // Validate 不触碰、消费点裸解引用，直接归一为空袋（缺失即空、无语义分歧）
        var errors = new List<string>();
        if (catalog.Settings is null)
        {
            errors.Add("settings must be an object (got null).");
            catalog.Settings = new AppSettings();
        }

        if (catalog.Games is null)
        {
            errors.Add("games must be an array (got null).");
            catalog.Games = [];
        }

        for (var i = catalog.Games.Count - 1; i >= 0; i--)
        {
            if (catalog.Games[i] is not { } game)
            {
                errors.Add($"games[{i}] must be an object (got null).");
                catalog.Games.RemoveAt(i);
                continue;
            }

            if (game.Launch is null)
            {
                errors.Add($"games[{i}].launch must be an object (got null).");
                game.Launch = new LaunchOptions();
            }
            else
            {
                game.Launch.Environment ??= [];
            }

            if (game.Servers is null)
            {
                errors.Add($"games[{i}].servers must be an array (got null).");
                game.Servers = [];
            }
            else
            {
                for (var s = game.Servers.Count - 1; s >= 0; s--)
                {
                    if (game.Servers[s] is not { } server)
                    {
                        errors.Add($"games[{i}].servers[{s}] must be an object (got null).");
                        game.Servers.RemoveAt(s);
                        continue;
                    }

                    server.Options ??= [];
                }
            }

            if (game.NameLocalized is null)
            {
                errors.Add($"games[{i}].nameLocalized must be an object (got null).");
                game.NameLocalized = [];
            }
        }

        errors.AddRange(Validate(catalog));
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

        // 手动代理必须给出可解析的 http(s) 地址；其它模式地址可有可无
        if (settings.ProxyMode == ProxyMode.Manual)
        {
            var proxyValid = Uri.TryCreate(settings.ProxyAddress, UriKind.Absolute, out var proxy)
                && proxy.Scheme is "http" or "https";
            if (!proxyValid)
            {
                errors.Add("settings.proxyAddress must be an http(s)://host:port URL when proxyMode is manual.");
            }
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

        if (!string.IsNullOrWhiteSpace(game.Launch.UmuId) && !IsValidUmuId(game.Launch.UmuId))
        {
            errors.Add(
                $"{field}.launch.umuId must look like \"umu-<slug>\" (letters, digits, '-', '_' after the umu- prefix).");
        }

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

    /// <summary>umuId 须形如 umu-&lt;slug&gt;（后缀仅字母/数字/-/_），与 umu 数据库的 UMU_ID 规范一致。</summary>
    private static bool IsValidUmuId(string umuId) =>
        umuId.StartsWith("umu-", StringComparison.Ordinal)
        && umuId.Length > "umu-".Length
        && umuId["umu-".Length..].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
}
