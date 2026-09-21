using Xunit;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services.Umu;

/// <summary>
/// VDF 最小解析器直测（2026-09-22 测试审计补齐：此前仅经 NativeUmuCoreTests 间接覆盖，
/// 转义引号/裸 token/注释/取值缺失路径全部未测——工具清单读取的正确性靠它兜底）。
/// </summary>
public class VdfMiniParserTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public void Parse_ToolmanifestShapedInput_BuildsNestedDictionaries()
    {
        // 真实 toolmanifest.vdf 形态：顶层键 + 嵌套对象 + 多层值
        var root = VdfMiniParser.Parse("""
            "manifest"
            {
              "version" "2"
              "commandline" "/proton waitforexitandrun %verb%"
              "require_tool_appid" "0"
            }
            """);

        var manifest = Assert.IsType<Dictionary<string, object>>(root["manifest"]);
        Assert.Equal("2", manifest["version"]);
        Assert.Equal("/proton waitforexitandrun %verb%", manifest["commandline"]);
        Assert.Equal("0", manifest["require_tool_appid"]);
    }

    [Fact]
    public void Parse_EscapedQuoteAndBackslash_UnescapeToLiteral()
    {
        var root = VdfMiniParser.Parse("\"key\" \"a\\\"b\\\\c\"");

        // \" → "，\\ → \（tokenizer 的反斜杠转义分支）
        Assert.Equal("a\"b\\c", root["key"]);
    }

    [Fact]
    public void Parse_UnterminatedString_TakesRestOfText()
    {
        // 容错语义：右引号缺失时不抛，吞到文本末尾（对不可信输入不崩溃）
        var root = VdfMiniParser.Parse("\"key\" \"trailing value");

        Assert.Equal("trailing value", root["key"]);
    }

    [Fact]
    public void Parse_CommentLines_Skipped()
    {
        var root = VdfMiniParser.Parse("""
            // 顶部注释
            "a" "1"
            "b" // 行尾注释
            {
              "c" "2"
            }
            """);

        Assert.Equal("1", root["a"]);
        var b = Assert.IsType<Dictionary<string, object>>(root["b"]);
        Assert.Equal("2", b["c"]);
    }

    [Fact]
    public void Parse_BareTokens_AcceptedAsKeyValue()
    {
        // 少见的无引号形态（部分 Valve 工具产出）
        var root = VdfMiniParser.Parse("key value");

        Assert.Equal("value", root["key"]);
    }

    [Fact]
    public void Parse_DuplicateKeys_LastWins()
    {
        var root = VdfMiniParser.Parse("\"k\" \"1\"\n\"k\" \"2\"");

        Assert.Equal("2", root["k"]);
    }

    [Fact]
    public void Parse_StrayCloseBrace_ReturnsEarly()
    {
        var root = VdfMiniParser.Parse("\"a\" \"1\"\n}\n\"b\" \"2\"");

        // 多余的 } 结束当前对象：其后的 b 丢失（容错优先，不抛）
        Assert.Equal("1", root["a"]);
        Assert.False(root.ContainsKey("b"));
    }

    [Fact]
    public void Parse_TrailingKeyWithoutValue_Dropped()
    {
        var root = VdfMiniParser.Parse("\"a\" \"1\"\n\"orphan\"");

        Assert.Equal("1", root["a"]);
        Assert.False(root.ContainsKey("orphan"));
    }

    [Fact]
    public void ParseFile_ReadsAndParsesFromDisk()
    {
        var path = _tempDir.FilePath("toolmanifest.vdf");
        File.WriteAllText(path, "\"manifest\"\n{\n  \"version\" \"2\"\n}\n");

        var root = VdfMiniParser.ParseFile(path);

        var manifest = Assert.IsType<Dictionary<string, object>>(root["manifest"]);
        Assert.Equal("2", manifest["version"]);
    }

    [Fact]
    public void GetObject_ValidPath_ReturnsNestedTable()
    {
        var root = VdfMiniParser.Parse("\"a\"\n{\n  \"b\"\n  {\n    \"c\" \"1\"\n  }\n}");

        var b = VdfMiniParser.GetObject(root, "a", "b");

        Assert.NotNull(b);
        Assert.Equal("1", b["c"]);
    }

    [Fact]
    public void GetObject_MissingKeyOrNonTableStep_ReturnsNull()
    {
        var root = VdfMiniParser.Parse("\"a\" \"scalar\"");

        Assert.Null(VdfMiniParser.GetObject(root, "missing"));
        Assert.Null(VdfMiniParser.GetObject(root, "a")); // 中途是标量，不是子表
        Assert.Null(VdfMiniParser.GetObject(root, "a", "b"));
    }

    [Fact]
    public void GetString_RootLeafAndNestedPath()
    {
        var root = VdfMiniParser.Parse("\"top\" \"v0\"\n\"a\"\n{\n  \"b\" \"v1\"\n}");

        Assert.Equal("v0", VdfMiniParser.GetString(root, "top"));
        Assert.Equal("v1", VdfMiniParser.GetString(root, "a", "b"));
    }

    [Fact]
    public void GetString_MissingOrNonString_ReturnsNull()
    {
        var root = VdfMiniParser.Parse("\"a\"\n{\n  \"table\" { }\n}");

        Assert.Null(VdfMiniParser.GetString(root, "missing"));
        Assert.Null(VdfMiniParser.GetString(root, "a", "missing"));
        Assert.Null(VdfMiniParser.GetString(root, "a", "table")); // 叶子是子表不是字符串
        Assert.Null(VdfMiniParser.GetString(root));
    }
}
