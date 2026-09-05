using System.Globalization;
using System.Text.Json;
using YetAnotherGameLauncher.Services;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

public class LocalizationServiceTests
{
    [Fact]
    public void Default_ReturnsChinese()
    {
        var loc = new LocalizationService();

        Assert.Equal("zh-CN", loc.Language);
        Assert.Equal("设置", loc["common_settings"]);
    }

    [Fact]
    public void SetLanguage_English_ReturnsEnglish()
    {
        var loc = new LocalizationService();

        loc.SetLanguage("en-US");

        Assert.Equal("en-US", loc.Language);
        Assert.Equal("Settings", loc["common_settings"]);
    }

    [Fact]
    public void SetLanguage_System_ResolvesCurrentUICulture()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            var loc = new LocalizationService();

            CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
            loc.SetLanguage(LocalizationService.SystemLanguage);
            Assert.Equal("设置", loc["common_settings"]);

            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            loc.SetLanguage(LocalizationService.SystemLanguage);
            Assert.Equal("Settings", loc["common_settings"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void UnknownKey_ReturnsKeyItself()
    {
        var loc = new LocalizationService();

        Assert.Equal("no.such.key", loc["no.such.key"]);
    }

    [Fact]
    public void UnknownLanguage_FallsBackToDefault()
    {
        var loc = new LocalizationService();

        loc.SetLanguage("fr-FR");

        Assert.Equal(LocalizationService.DefaultLanguage, loc.Language);
        Assert.Equal("设置", loc["common_settings"]);
    }

    [Fact]
    public void Format_NoArgs_KeepsCurlyPlaceholders()
    {
        var loc = new LocalizationService();

        // {exe} 不是 string.Format 格式槽：无参数时必须原样返回
        Assert.Equal("可用占位符：{exe}、{installDir}", loc.Format("launch_commandTemplateHint"));
    }

    [Fact]
    public void Format_WithArgs_Interpolates()
    {
        var loc = new LocalizationService();

        Assert.Equal("3 款游戏", loc.Format("sidebar_games_count", 3));
    }

    [Fact]
    public void SetLanguage_RaisesItemNotification()
    {
        var loc = new LocalizationService();
        var properties = new List<string>();
        loc.PropertyChanged += (_, e) => properties.Add(e.PropertyName ?? "");

        loc.SetLanguage("en-US");

        Assert.Contains("Item[]", properties);
    }

    [Fact]
    public void LanguageResources_HaveParity()
    {
        // 防漏译：默认语言与各翻译资源的键集必须完全一致
        var cultures = new[] { "zh-CN", "en-US" };
        Dictionary<string, string>? baseline = null;

        foreach (var culture in cultures)
        {
            var strings = LoadResource($"YetAnotherGameLauncher.Resources.strings_{culture}.json");
            if (baseline is null)
            {
                baseline = strings;
                Assert.NotEmpty(baseline);
            }
            else
            {
                Assert.Equal(baseline.Keys, strings.Keys);
            }
        }
    }

    private static Dictionary<string, string> LoadResource(string name)
    {
        using var stream = typeof(LocalizationService).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream!)
               ?? throw new InvalidOperationException($"资源 {name} 不是有效的键值 JSON。");
    }
}
