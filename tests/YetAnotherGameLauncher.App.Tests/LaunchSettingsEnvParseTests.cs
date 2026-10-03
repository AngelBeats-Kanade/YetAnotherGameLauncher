using Xunit;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 启动设置卡「自定义启动选项」解析边界：宽松版（草稿合并用，跳过坏条目）与严格版（保存校验用，
/// 报坏条目）共用 <see cref="LaunchOptionsText"/> 的 Steam 风格词法（空白分隔、引号包裹、反斜杠转义）。
/// 2026-10-03 语义变更：旧「每行一条 KEY=VALUE、= 两侧容忍空格」→ shell 风格——未加引号的空格
/// 一律是分隔符，含空格的值必须加引号。2026-10-03 二次扩展：显式 %command% 之前为环境区
/// （KEY=VALUE），之后为游戏参数（宽松切分保留参数不丢，严格版按错误区分类路由文案）。
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
    public void Parse_WhitespaceAroundEntries_LastDuplicateWins()
    {
        var parsed = LaunchSettingsViewModel.ParseEnvironmentOrEmpty(
            "A=1\r\n  B=\"two\"  \nA=3\r\n");

        Assert.Equal(2, parsed.Count);
        Assert.Equal("3", parsed["A"]); // 重复键：后值覆盖（字典赋值语义）
        Assert.Equal("two", parsed["B"]);
    }

    [Fact]
    public void Parse_SteamStyleSingleLine_ExtractsAllEntries()
    {
        // 2026-10-03 用户实际输入场景：单行空格分隔 + 引号值（旧解析器整行吞成一条）
        var parsed = LaunchSettingsViewModel.ParseEnvironmentOrEmpty(
            "DXVK_NVAPI_DRS_SETTINGS=\"NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION=13\""
            + " PROTON_DXVK_LLASYNC=1 PROTON_ENABLE_WAYLAND=1 OBS_VKCAPTURE=1");

        Assert.Equal(4, parsed.Count);
        Assert.Equal("NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION=13", parsed["DXVK_NVAPI_DRS_SETTINGS"]);
        Assert.Equal("1", parsed["PROTON_DXVK_LLASYNC"]);
        Assert.Equal("1", parsed["PROTON_ENABLE_WAYLAND"]);
        Assert.Equal("1", parsed["OBS_VKCAPTURE"]);
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
    public void TryParse_QuotedValueWithSpaces_Valid()
    {
        var ok = LaunchSettingsViewModel.TryParseLaunchText(
            "MAP=\"coast 11\"", out var parsed, out var arguments, out _, out var errorKind);

        Assert.True(ok);
        Assert.Equal(LaunchLineErrorKind.None, errorKind);
        Assert.Equal("coast 11", parsed["MAP"]);
        Assert.Empty(arguments);
    }

    [Fact]
    public void TryParse_UnquotedSpaceInValue_ReportsTrailingTokenAsBad()
    {
        // 2026-10-03 语义变更钉：未加引号的值内空格不再吞进值，尾随裸 token 报错（提示用户加引号）；
        // F-F：裸 token 单独分类（文案指路 %command%）
        var ok = LaunchSettingsViewModel.TryParseLaunchText(
            "MAP=coast 11", out _, out _, out var badLine, out var errorKind);

        Assert.False(ok);
        Assert.Equal(LaunchLineErrorKind.EnvironmentBareToken, errorKind);
        Assert.Equal("11", badLine);
    }

    [Fact]
    public void TryParse_EmptyValueIsKeptAndValid()
    {
        // 空值是合法环境变量（如 WINEDEBUG= 表示清空默认通道），不得当坏行
        var ok = LaunchSettingsViewModel.TryParseLaunchText(
            "KEY=", out var parsed, out _, out var badLine, out _);

        Assert.True(ok);
        Assert.Equal("", badLine);
        Assert.Equal("", parsed["KEY"]);
    }

    [Fact]
    public void TryParse_BareTokenWithoutKey_HasDedicatedErrorKind()
    {
        // F-F：裸 token（无 =）单独分类——多为想给游戏传参的误解，文案指路 %command%；
        // 与空键名（沿用 KEY=VALUE 提示）区分
        var ok = LaunchSettingsViewModel.TryParseLaunchText(
            "NOEQ", out _, out _, out var badLine, out var errorKind);

        Assert.False(ok);
        Assert.Equal(LaunchLineErrorKind.EnvironmentBareToken, errorKind);
        Assert.Equal("NOEQ", badLine);
    }

    [Theory]
    [InlineData("=novalue")] // 空键 = 键名缺失，保存时必须拒绝
    [InlineData("  =novalue")]
    public void TryParse_RejectsEmptyKey_WithKeyValueError(string text)
    {
        var ok = LaunchSettingsViewModel.TryParseLaunchText(
            text, out _, out _, out var badLine, out var errorKind);

        Assert.False(ok);
        Assert.Equal(LaunchLineErrorKind.EnvironmentItem, errorKind);
        Assert.Equal(text.Trim(), badLine);
    }

    [Fact]
    public void TryParse_Multiline_ReportsFirstBadLineAndStops()
    {
        var ok = LaunchSettingsViewModel.TryParseLaunchText(
            "OK=1\r\nBAD\r\nKEY=v", out var parsed, out _, out var badLine, out _);

        Assert.False(ok);
        Assert.Equal("BAD", badLine);
        Assert.True(parsed.ContainsKey("OK")); // 逐条解析：坏条目之前的条目已进字典（保存侧整体弃用）
        Assert.False(parsed.ContainsKey("KEY")); // 坏条目即停：之后的内容不再解析
    }

    [Fact]
    public void TryParse_BlankLinesAndWhitespaceAroundEntries_Tolerated()
    {
        var ok = LaunchSettingsViewModel.TryParseLaunchText(
            "\r\n  LANG=zh_CN.UTF-8  \r\n\t\r\n", out var parsed, out _, out _, out _);

        Assert.True(ok);
        Assert.Single(parsed);
        Assert.Equal("LANG", parsed.Keys.Single());
        Assert.Equal("zh_CN.UTF-8", parsed["LANG"]);
    }

    [Fact]
    public void TryParse_TextWithPlaceholder_SplitsEnvironmentAndArguments()
    {
        // 2026-10-03 Steam 语义：显式 %command% 之前是环境区（KEY=VALUE），之后是游戏参数
        var ok = LaunchSettingsViewModel.TryParseLaunchText(
            "MAP=\"coast 11\"\r\n%command% -dx11 \"a b\"",
            out var parsed, out var arguments, out _, out _);

        Assert.True(ok);
        Assert.Single(parsed);
        Assert.Equal("coast 11", parsed["MAP"]);
        Assert.Equal(new[] { "-dx11", "a b" }, arguments);
    }

    [Fact]
    public void TryParse_DuplicatePlaceholder_ReportsErrorKind()
    {
        // 错误分类路由到专属文案（最多出现一次），不落"应为 KEY=VALUE"的误导提示
        var ok = LaunchSettingsViewModel.TryParseLaunchText(
            "A=1 %command% %command%", out _, out _, out _, out var errorKind);

        Assert.False(ok);
        Assert.Equal(LaunchLineErrorKind.DuplicateCommandPlaceholder, errorKind);
    }

    [Fact]
    public void ParseLaunchTextOrEmpty_KeepsArgumentsAndSkipsBadEnv()
    {
        // 宽松切分供切启动方式/发行版时重写文本：坏条目跳过、%command% 参数保留不丢
        var arguments = LaunchSettingsViewModel.ParseLaunchTextOrEmpty(
            "半截 A=1 %command% -dx11", out var parsed);

        Assert.Single(parsed);
        Assert.Equal("1", parsed["A"]);
        Assert.Equal(new[] { "-dx11" }, arguments);
    }
}
