using Xunit;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 启动设置卡环境变量解析边界（Phase 4b，2026-09-19，审计缺口 LaunchSettingsVM :559-633）：
/// 宽松版（草稿合并用，跳过坏行）与严格版（保存校验用，报坏行）的切分规则逐一锁定。
/// </summary>
public class LaunchSettingsEnvParseTests
{
    [Fact]
    public void Parse_EmptyOrWhitespace_ReturnsEmptyDictionary()
    {
        Assert.Empty(LaunchSettingsViewModel.ParseEnvironmentOrEmpty(""));
        Assert.Empty(LaunchSettingsViewModel.ParseEnvironmentOrEmpty("\r\n  \n\t"));
    }

    [Fact]
    public void Parse_TrimsKeysAndValues_LastDuplicateWins()
    {
        var parsed = LaunchSettingsViewModel.ParseEnvironmentOrEmpty(
            "A=1\r\nB = two \nA=3\r\n");

        Assert.Equal(2, parsed.Count);
        Assert.Equal("3", parsed["A"]); // 重复键：后值覆盖（字典赋值语义）
        Assert.Equal("two", parsed["B"]);
    }

    [Fact]
    public void Parse_SkipsLinesWithoutEquals_KeepsRest()
    {
        // 宽松版用于草稿合并（切换发行版/启动方式时不得因用户敲了一半的行而丢数据）
        var parsed = LaunchSettingsViewModel.ParseEnvironmentOrEmpty(
            "半截行\r\nOK=1\r\n");

        Assert.Single(parsed);
        Assert.Equal("1", parsed["OK"]);
    }

    [Fact]
    public void Parse_ValueMayContainEquals_SplitOnFirstSeparatorOnly()
    {
        var parsed = LaunchSettingsViewModel.ParseEnvironmentOrEmpty("URL=http://x?a=b&c=d");

        Assert.Equal("http://x?a=b&c=d", parsed["URL"]);
    }

    [Fact]
    public void TryParse_EmptyValueIsKeptAndValid()
    {
        // 空值是合法环境变量（如 WINEDEBUG= 表示清空默认通道），不得当坏行
        var ok = LaunchSettingsViewModel.TryParseEnvironment("KEY=", out var parsed, out var badLine);

        Assert.True(ok);
        Assert.Equal("", badLine);
        Assert.Equal("", parsed["KEY"]);
    }

    [Theory]
    [InlineData("NOEQ")]
    [InlineData("=novalue")] // 空键 = 键名缺失，保存时必须拒绝
    [InlineData("  =novalue")]
    public void TryParse_RejectsLinesWithoutKeyOrSeparator(string text)
    {
        var ok = LaunchSettingsViewModel.TryParseEnvironment(text, out _, out var badLine);

        Assert.False(ok);
        Assert.Equal(text.Trim(), badLine);
    }

    [Fact]
    public void TryParse_Multiline_ReportsFirstBadLineAndStops()
    {
        var ok = LaunchSettingsViewModel.TryParseEnvironment("OK=1\r\nBAD\r\nKEY=v", out var parsed, out var badLine);

        Assert.False(ok);
        Assert.Equal("BAD", badLine);
        Assert.True(parsed.ContainsKey("OK")); // 逐行解析：坏行之前的行已进字典（保存侧整体弃用）
        Assert.False(parsed.ContainsKey("KEY")); // 坏行即停：之后的内容不再解析
    }

    [Fact]
    public void TryParse_BlankLinesAndWhitespaceAroundEntries_Tolerated()
    {
        var ok = LaunchSettingsViewModel.TryParseEnvironment(
            "\r\n  LANG = zh_CN.UTF-8  \r\n\t\r\n", out var parsed, out _);

        Assert.True(ok);
        Assert.Single(parsed);
        Assert.Equal("LANG", parsed.Keys.Single());
        Assert.Equal("zh_CN.UTF-8", parsed["LANG"]);
    }
}
