using System.Globalization;
using System.Text.Json;
using Xunit;
using YetAnotherGameLauncher.Services;

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
    public void SetLanguage_UnprovidedEnVariant_FallsBackToDefaultStrings()
    {
        // 资源缺失分支：Normalize 放行一切 en 前缀语言，但程序集只嵌 zh-CN/en-US 两份资源——
        // Load 返回空集，索引器逐键回退默认语言文案，界面不得因缺资源挂掉
        var loc = new LocalizationService();

        loc.SetLanguage("en-GB");

        Assert.Equal("en-GB", loc.Language);
        Assert.Equal("设置", loc["common_settings"]); // 回退默认（zh-CN）文案
    }

    [Fact]
    public void SetLanguage_BareEn_NormalizesToEnUs()
    {
        // 次级 suspect（第 9 轮，artifacts/bugs.md）：裸 "en"（手改 games.json 才可达的防御缺口）
        // 原样放行会 Load 空集逐键回退中文——归一到 en-US 直接命中既有英文资源
        var loc = new LocalizationService();

        loc.SetLanguage("en");

        Assert.Equal("en-US", loc.Language);
        Assert.Equal("Settings", loc["common_settings"]);
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
