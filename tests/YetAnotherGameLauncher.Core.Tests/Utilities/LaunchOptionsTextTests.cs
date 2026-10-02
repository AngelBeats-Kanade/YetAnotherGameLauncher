using Xunit;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Tests.Utilities;

/// <summary>
/// 自定义启动选项文本解析器（Steam 启动选项风格）：空白（含换行）分隔多条 KEY=VALUE，
/// 引号包裹含空格的值，反斜杠转义。严格版（保存校验，坏条目报错）与宽松版（草稿合并，
/// 坏条目跳过）共用同一词法。2026-10-03 语义变更：旧「每行一条 KEY=VALUE、= 两侧容忍空格」
/// 改为 shell 风格词法——未加引号的空格一律是分隔符，含空格的值必须加引号。
/// </summary>
public class LaunchOptionsTextTests
{
    [Fact]
    public void TryParse_UserExample_SingleLineFourEntries_StripsQuotes()
    {
        // 2026-10-03 用户实际输入：值内含 = 且带引号，同单行空格分隔四条——旧解析器整行吞成一条
        var ok = LaunchOptionsText.TryParse(
            "DXVK_NVAPI_DRS_SETTINGS=\"NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION=13\""
            + " PROTON_DXVK_LLASYNC=1 PROTON_ENABLE_WAYLAND=1 OBS_VKCAPTURE=1",
            out var parsed, out var badItem);

        Assert.True(ok);
        Assert.Equal("", badItem);
        Assert.Equal(4, parsed.Count);
        Assert.Equal("NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION=13", parsed["DXVK_NVAPI_DRS_SETTINGS"]);
        Assert.Equal("1", parsed["PROTON_DXVK_LLASYNC"]);
        Assert.Equal("1", parsed["PROTON_ENABLE_WAYLAND"]);
        Assert.Equal("1", parsed["OBS_VKCAPTURE"]);
    }

    [Fact]
    public void TryParse_NewlineAndSpaceSeparators_ProduceSameResult()
    {
        // 回归锚：旧「每行一条」格式在新词法下语义不变（换行也是分隔符）
        LaunchOptionsText.TryParse("A=1\r\nB=2\nC=3", out var fromLines, out _);
        LaunchOptionsText.TryParse("A=1 B=2 C=3", out var fromSpaces, out _);

        AssertEqual(fromLines, fromSpaces);
    }

    [Fact]
    public void TryParse_EmptyOrWhitespace_ReturnsTrueWithEmptyDictionary()
    {
        var ok = LaunchOptionsText.TryParse("\r\n  \n\t", out var parsed, out var badItem);

        Assert.True(ok);
        Assert.Equal("", badItem);
        Assert.Empty(parsed);
    }

    [Fact]
    public void TryParse_DoubleQuotedValue_KeepsSpacesAndInnerEquals()
    {
        var ok = LaunchOptionsText.TryParse("MAP=\"coast 11\"\r\nURL=\"http://x?a=b&c=d\"", out var parsed, out _);

        Assert.True(ok);
        Assert.Equal("coast 11", parsed["MAP"]);
        Assert.Equal("http://x?a=b&c=d", parsed["URL"]);
    }

    [Fact]
    public void TryParse_SingleQuotedValue_AllLiteral_NoEscaping()
    {
        var ok = LaunchOptionsText.TryParse("KEY='a \"b\" \\c'", out var parsed, out _);

        Assert.True(ok);
        Assert.Equal("a \"b\" \\c", parsed["KEY"]);
    }

    [Fact]
    public void TryParse_EscapesOutsideQuotes_NextCharacterIsLiteral()
    {
        var ok = LaunchOptionsText.TryParse("KEY=a\\ b K2=a\\\"b K3=\\\\", out var parsed, out _);

        Assert.True(ok);
        Assert.Equal("a b", parsed["KEY"]);
        Assert.Equal("a\"b", parsed["K2"]);
        Assert.Equal("\\", parsed["K3"]);
    }

    [Fact]
    public void TryParse_BackslashInsideDoubleQuotes_EscapesQuoteAndBackslashOnly()
    {
        // 双引号内反斜杠只在 " 与 \ 前生效；其余（含 \$）原样保留——不做变量展开，$ 恒为字面
        var ok = LaunchOptionsText.TryParse("K=\"a\\ b \\\" \\\\ \\$\"", out var parsed, out _);

        Assert.True(ok);
        Assert.Equal("a\\ b \" \\ \\$", parsed["K"]);
    }

    [Fact]
    public void TryParse_QuotedValueSpanningLines_NewlineIsPartOfValue()
    {
        var ok = LaunchOptionsText.TryParse("KEY=\"a\nb\"", out var parsed, out _);

        Assert.True(ok);
        Assert.Equal("a\nb", parsed["KEY"]);
    }

    [Fact]
    public void TryParse_DuplicateKey_LastWins()
    {
        var ok = LaunchOptionsText.TryParse("A=1 A=2", out var parsed, out _);

        Assert.True(ok);
        Assert.Equal("2", parsed["A"]);
    }

    [Fact]
    public void TryParse_EmptyValue_BothBareAndQuoted_Kept()
    {
        // 空值是合法环境变量（如 WINEDEBUG= 表示清空默认通道），不得当坏条目
        var ok = LaunchOptionsText.TryParse("KEY= K2=\"\"", out var parsed, out _);

        Assert.True(ok);
        Assert.Equal("", parsed["KEY"]);
        Assert.Equal("", parsed["K2"]);
    }

    [Fact]
    public void TryParse_UnterminatedDoubleQuote_FailsWithRawSpan_StopsAtBadItem()
    {
        var ok = LaunchOptionsText.TryParse("OK=1 K=\"unclosed", out var parsed, out var badItem);

        Assert.False(ok);
        Assert.Equal("K=\"unclosed", badItem);
        Assert.True(parsed.ContainsKey("OK")); // 坏条目之前的条目已进字典（保存侧整体弃用）
        Assert.False(parsed.ContainsKey("K"));
    }

    [Fact]
    public void TryParse_UnterminatedSingleQuote_FailsWithRawSpan()
    {
        var ok = LaunchOptionsText.TryParse("K='abc", out var parsed, out var badItem);

        Assert.False(ok);
        Assert.Equal("K='abc", badItem);
        Assert.Empty(parsed);
    }

    [Fact]
    public void TryParse_BareTokenWithoutEquals_FailsWithRawSpan()
    {
        var ok = LaunchOptionsText.TryParse("OK=1 NOEQ", out var parsed, out var badItem);

        Assert.False(ok);
        Assert.Equal("NOEQ", badItem);
        Assert.True(parsed.ContainsKey("OK"));
    }

    [Theory]
    [InlineData("=v", "=v")]
    [InlineData("  =v", "=v")] // 空键名 = 键名缺失，保存时必须拒绝；坏条目原文按首尾去空白报告
    public void TryParse_EmptyKey_FailsWithRawSpan(string text, string expectedBadItem)
    {
        var ok = LaunchOptionsText.TryParse(text, out var parsed, out var badItem);

        Assert.False(ok);
        Assert.Equal(expectedBadItem, badItem);
        Assert.Empty(parsed);
    }

    [Fact]
    public void ParseLenient_SkipsBadItems_KeepsGoodEntries()
    {
        // 宽松版用于草稿合并（切换发行版/启动方式时不得因用户敲了一半的条目而丢数据）
        var parsed = LaunchOptionsText.ParseLenient("半截行\r\nOK=1\r\n=bad\r\nGOOD=2\r\n");

        Assert.Equal(2, parsed.Count);
        Assert.Equal("1", parsed["OK"]);
        Assert.Equal("2", parsed["GOOD"]);
    }

    [Fact]
    public void ParseLenient_UnterminatedQuote_DropsTrailingGarbage()
    {
        // 未闭合引号吞到文末：宽松版丢弃该残条目，保留之前的条目
        var parsed = LaunchOptionsText.ParseLenient("OK=1 \"unclosed OTHER=2");

        Assert.Single(parsed);
        Assert.Equal("1", parsed["OK"]);
    }

    [Fact]
    public void Serialize_PlainEntries_OnePerLine_Unquoted()
    {
        var text = LaunchOptionsText.Serialize(new Dictionary<string, string>
        {
            ["A"] = "1",
            ["B"] = "",
        });

        Assert.Equal($"A=1{Environment.NewLine}B=", text);
    }

    [Fact]
    public void Serialize_SpecialValues_QuotedEscaped_AndRoundTrips()
    {
        var original = new Dictionary<string, string>
        {
            ["MAP"] = "coast 11",
            ["Q"] = "a\"b",
            ["SQ"] = "a'b", // 单引号也是词法开启符，必须进加引号判据（RF-14，2026-10-03 复审 fuzz 实锤）
            ["BS"] = "a\\b",
            ["EMPTY"] = "",
            ["NL"] = "a\nb",
        };

        var text = LaunchOptionsText.Serialize(original);
        var ok = LaunchOptionsText.TryParse(text, out var reparsed, out var badItem);

        Assert.True(ok, $"回显文本应可无损重解析，实际坏条目：{badItem}，文本：{text}");
        AssertEqual(original, reparsed);
    }

    [Fact]
    public void Serialize_Parse_RoundTripFuzz_DeterministicSeed()
    {
        // 确定性种子 fuzz：值域覆盖词法全部特殊字符（空白/双单引号/反斜杠/等号/普通字符）。
        // Random(种子) 序列在同一运行时内确定；断言的是往返不变量本身，序列漂移无害。
        // 2026-10-03 复审以 3000 次同款实验实锤 RF-14（单引号漏判），缩小规模入库常驻。
        var rnd = new Random(42);
        var alphabet = new[] { 'a', ' ', '\t', '"', '\'', '\\', '=', '\n', '\r', '$', '　' };
        for (var iter = 0; iter < 1000; iter++)
        {
            var dict = new Dictionary<string, string>();
            var entryCount = rnd.Next(1, 4);
            for (var i = 0; i < entryCount; i++)
            {
                var valueLength = rnd.Next(0, 8);
                var chars = new char[valueLength];
                for (var j = 0; j < valueLength; j++)
                {
                    chars[j] = alphabet[rnd.Next(alphabet.Length)];
                }

                dict[$"K{i}"] = new string(chars);
            }

            var text = LaunchOptionsText.Serialize(dict);
            var ok = LaunchOptionsText.TryParse(text, out var reparsed, out var badItem);

            Assert.True(ok, $"第 {iter} 轮往返失败，坏条目：{badItem}，文本：{text.Replace("\n", "\\n").Replace("\r", "\\r")}");
            AssertEqual(dict, reparsed);
        }
    }

    [Fact]
    public void TryParse_TrailingBackslashAtEof_IsLiteral()
    {
        // 文末孤立反斜杠按字面保留（RF-15，2026-10-03 复审补钉——该分支此前仅探测验证、无 repo 测试）
        var ok = LaunchOptionsText.TryParse("KEY=a\\", out var parsed, out _);

        Assert.True(ok);
        Assert.Equal("a\\", parsed["KEY"]);
    }

    [Fact]
    public void Serialize_EmptyDictionary_ReturnsEmptyString()
    {
        Assert.Equal("", LaunchOptionsText.Serialize(new Dictionary<string, string>()));
    }

    /// <summary>字典逐键断言（xunit 对 Dictionary 的整体相等比较按枚举序处理，逐键更稳）。</summary>
    private static void AssertEqual(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, value) in expected)
        {
            Assert.True(actual.TryGetValue(key, out var actualValue), $"缺少键 {key}");
            Assert.Equal(value, actualValue);
        }
    }
}
