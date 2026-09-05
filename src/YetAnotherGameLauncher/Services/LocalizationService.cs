using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// JSON 资源驱动的本地化服务。zh-CN 为默认语言（同时是缺键回退源），
/// 资源文件以 EmbeddedResource 内嵌于本程序集（Resources/strings.*.json）。
/// 语言切换通过 Item[] 通知驱动所有 {Binding Loc[key]} 绑定刷新。
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    /// <summary>表示"跟随系统"的设置值，写入 settings.language。</summary>
    public const string SystemLanguage = "system";

    /// <summary>默认语言：资源全集所在，缺键回退源。</summary>
    public const string DefaultLanguage = "zh-CN";

    private const string ResourceTemplate = "YetAnotherGameLauncher.Resources.strings_{0}.json";

    private static readonly Dictionary<string, string> DefaultStrings = Load(DefaultLanguage);

    private Dictionary<string, string> _strings = DefaultStrings;
    private string _language = DefaultLanguage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language => _language;

    public string EffectiveCulture => _language == SystemLanguage ? ResolveSystemCulture() : _language;

    public string this[string key] =>
        _strings.GetValueOrDefault(key) ?? DefaultStrings.GetValueOrDefault(key) ?? key;

    public string Format(string key, params object?[] args)
        => args.Length == 0 ? this[key] : string.Format(CultureInfo.CurrentUICulture, this[key], args);

    public void SetLanguage(string language)
    {
        var requested = Normalize(language); // "system" 或具体 culture
        var effective = requested == SystemLanguage ? ResolveSystemCulture() : requested;
        _strings = effective == DefaultLanguage ? DefaultStrings : Load(effective);
        _language = requested;
        // Avalonia 索引器绑定监听 "Item"（不同于 WPF 的 "Item[]"），两者都发保证刷新
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
    }

    private static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Trim() == SystemLanguage)
        {
            return SystemLanguage;
        }

        var culture = language.Trim();
        return culture == DefaultLanguage || culture.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? culture
            : DefaultLanguage; // 未提供的语言回退默认，避免整页缺译
    }

    private static string ResolveSystemCulture()
    {
        var name = CultureInfo.CurrentUICulture.Name;
        return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? DefaultLanguage : "en-US";
    }

    private static Dictionary<string, string> Load(string culture)
    {
        try
        {
            using var stream = typeof(LocalizationService).Assembly
                .GetManifestResourceStream(string.Format(CultureInfo.InvariantCulture, ResourceTemplate, culture));
            if (stream is null)
            {
                return [];
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
        catch (JsonException)
        {
            return []; // 资源损坏时降级为空集（缺键回退机制会兜住），不让界面挂掉
        }
    }
}
