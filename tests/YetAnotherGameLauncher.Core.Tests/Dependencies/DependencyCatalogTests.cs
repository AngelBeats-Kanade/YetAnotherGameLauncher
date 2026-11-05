using Xunit;
using YetAnotherGameLauncher.Core.Dependencies;

namespace YetAnotherGameLauncher.Core.Tests.Dependencies;

/// <summary>
/// 依赖清单解析与校验：错误聚合、字段规则、内嵌 cjk-fonts 清单自检。
/// </summary>
public class DependencyCatalogTests
{
    private const string ValidJson = """
        [
          {
            "id": "cjk-fonts",
            "version": "2.004R",
            "downloadUrl": "https://github.com/adobe-fonts/source-han-sans/releases/download/2.004R/SourceHanSans.ttc.zip",
            "fileName": "SourceHanSans.ttc.zip",
            "md5": "ce3fc169af61a4e7bc650e7e78186330",
            "sizeBytes": 95184312,
            "fonts": [ { "file": "SourceHanSans.ttc", "families": ["Source Han Sans SC", "Source Han Sans TC"] } ],
            "replacementGroups": [ { "target": "Source Han Sans SC", "replaces": ["SimSun", "SimHei"] } ]
          }
        ]
        """;

    [Fact]
    public void Parse_ValidManifest_MapsAllFields()
    {
        var manifests = DependencyCatalog.Parse(ValidJson);

        var manifest = Assert.Single(manifests);
        Assert.Equal("cjk-fonts", manifest.Id);
        Assert.Equal("2.004R", manifest.Version);
        Assert.Equal(
            "https://github.com/adobe-fonts/source-han-sans/releases/download/2.004R/SourceHanSans.ttc.zip",
            manifest.DownloadUrl);
        Assert.Equal("SourceHanSans.ttc.zip", manifest.FileName);
        Assert.Equal("ce3fc169af61a4e7bc650e7e78186330", manifest.Md5);
        Assert.Equal(95184312, manifest.SizeBytes);
        var font = Assert.Single(manifest.Fonts!);
        Assert.Equal("SourceHanSans.ttc", font.File);
        Assert.Equal(["Source Han Sans SC", "Source Han Sans TC"], font.Families);
        var group = Assert.Single(manifest.ReplacementGroups!);
        Assert.Equal("Source Han Sans SC", group.Target);
        Assert.Equal(["SimSun", "SimHei"], group.Replaces);
    }

    [Fact]
    public void Parse_AggregatesAllValidationErrors()
    {
        const string json = """
            [
              { "id": "Bad_Id", "version": "", "downloadUrl": "http://a.example/x.zip", "fileName": "",
                "md5": "xyz", "sizeBytes": 0, "fonts": [], "replacementGroups": [] },
              { "id": "second", "version": "1", "downloadUrl": "https://a.example/x.zip", "fileName": "x.zip",
                "md5": "ce3fc169af61a4e7bc650e7e78186330", "sizeBytes": 5, "archiveEntry": "ignored-unknown-field",
                "fonts": [ { "file": "", "families": [] } ],
                "replacementGroups": [ { "target": "", "replaces": [""] } ] }
            ]
            """;

        var ex = Assert.Throws<DependencyCatalogException>(() => DependencyCatalog.Parse(json));

        // 一次性聚合全部错误：修复者不必逐轮试探
        Assert.Contains("Bad_Id", ex.Errors[0]);
        Assert.Contains(ex.Errors, e => e.Contains("id"));
        Assert.Contains(ex.Errors, e => e.Contains("version"));
        Assert.Contains(ex.Errors, e => e.Contains("https"));
        Assert.Contains(ex.Errors, e => e.Contains("fileName"));
        Assert.Contains(ex.Errors, e => e.Contains("md5"));
        Assert.Contains(ex.Errors, e => e.Contains("sizeBytes"));
        Assert.Contains(ex.Errors, e => e.Contains("fonts"));
        Assert.Contains(ex.Errors, e => e.Contains("replacementGroups"));
    }

    /// <summary>缺省键（STJ 反序列化为 null，无非空强制）必须进聚合错误而非 NRE 穿透。</summary>
    [Fact]
    public void Parse_MissingKeys_AggregatesErrorsInsteadOfThrowingNre()
    {
        const string json = """
            [
              { "id": "missing-fields" }
            ]
            """;

        var ex = Assert.Throws<DependencyCatalogException>(() => DependencyCatalog.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("version"));
        Assert.Contains(ex.Errors, e => e.Contains("downloadUrl"));
        Assert.Contains(ex.Errors, e => e.Contains("fileName"));
        Assert.Contains(ex.Errors, e => e.Contains("md5"));
        Assert.Contains(ex.Errors, e => e.Contains("sizeBytes"));
        Assert.Contains(ex.Errors, e => e.Contains("fonts"));
        Assert.Contains(ex.Errors, e => e.Contains("replacementGroups"));
    }

    /// <summary>顶层数组的 null 元素（JSON [null]）同样归一为聚合错误而非 NRE。</summary>
    [Fact]
    public void Parse_NullArrayElement_AggregatesError()
    {
        const string json = """
            [
              null,
              { "id": "ok", "version": "1", "downloadUrl": "https://a/x.zip", "fileName": "x.zip",
                "md5": "ce3fc169af61a4e7bc650e7e78186330", "sizeBytes": 5,
                "fonts": [ { "file": "a.ttc", "families": ["F"] } ], "replacementGroups": [] }
            ]
            """;

        var ex = Assert.Throws<DependencyCatalogException>(() => DependencyCatalog.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("清单条目不能为空"));
    }

    [Fact]
    public void Parse_FontFileWithPathSegments_Rejected()
    {
        const string json = """
            [
              { "id": "traversal", "version": "1", "downloadUrl": "https://a/x.zip", "fileName": "x.zip",
                "md5": "ce3fc169af61a4e7bc650e7e78186330", "sizeBytes": 5,
                "fonts": [ { "file": "../evil.ttc", "families": ["F"] } ], "replacementGroups": [] }
            ]
            """;

        var ex = Assert.Throws<DependencyCatalogException>(() => DependencyCatalog.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("fonts[].file"));
    }

    [Fact]
    public void Parse_DuplicateId_Fails()
    {
        const string json = """
            [
              { "id": "a", "version": "1", "downloadUrl": "https://a/x.zip", "fileName": "x.zip",
                "md5": "ce3fc169af61a4e7bc650e7e78186330", "sizeBytes": 5,
                "fonts": [ { "file": "a.ttc", "families": ["F"] } ], "replacementGroups": [] },
              { "id": "A", "version": "2", "downloadUrl": "https://b/x.zip", "fileName": "y.zip",
                "md5": "ce3fc169af61a4e7bc650e7e78186330", "sizeBytes": 5,
                "fonts": [ { "file": "a.ttc", "families": ["F"] } ], "replacementGroups": [] }
            ]
            """;

        var ex = Assert.Throws<DependencyCatalogException>(() => DependencyCatalog.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("a", StringComparison.OrdinalIgnoreCase) && e.Contains("重复"));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"id\": \"object-not-array\"}")]
    public void Parse_MalformedDocument_Fails(string json)
    {
        Assert.Throws<DependencyCatalogException>(() => DependencyCatalog.Parse(json));
    }

    /// <summary>合法 winetricks 来源清单：下载四字段与 fonts/replacementGroups 均为空，dlls 声明两份 DLL。</summary>
    private const string ValidWinetricksJson = """
        [
          {
            "id": "richedit-msftedit",
            "version": "6.1.7601.17514",
            "winetricksVerb": "msftedit",
            "dlls": [
              { "arch": "x64", "fileName": "msftedit.dll", "sizeBytes": 799744,
                "sha256": "e15ed4fefc3010c213694331ddfdc03767682325c898d773ab243e2dc8b08461" },
              { "arch": "x86", "fileName": "msftedit.dll", "sizeBytes": 592384,
                "sha256": "04ed66befdb822181ebd1d84cbf0b17aaadf8455ae742f44d7adcb26ab07bdad" }
            ],
            "dllOverrides": { "*msftedit": "native,builtin" },
            "fonts": null,
            "replacementGroups": null
          }
        ]
        """;

    [Fact]
    public void Parse_WinetricksManifest_AllowsNullFontsAndReplacementGroups()
    {
        var manifests = DependencyCatalog.Parse(ValidWinetricksJson);

        var manifest = Assert.Single(manifests);
        Assert.Equal("richedit-msftedit", manifest.Id);
        Assert.Equal("msftedit", manifest.WinetricksVerb);
        Assert.Null(manifest.DownloadUrl);
        Assert.Null(manifest.FileName);
        Assert.Null(manifest.Md5);
        Assert.Null(manifest.SizeBytes);
        Assert.Null(manifest.Fonts);
        Assert.Null(manifest.ReplacementGroups);
        Assert.Equal(2, manifest.Dlls!.Count);
        var x64 = Assert.Single(manifest.Dlls, d => d.Arch == "x64");
        Assert.Equal("msftedit.dll", x64.FileName);
        Assert.Equal(799744, x64.SizeBytes);
        Assert.Equal("e15ed4fefc3010c213694331ddfdc03767682325c898d773ab243e2dc8b08461", x64.Sha256);
        Assert.Equal("native,builtin", manifest.DllOverrides!["*msftedit"]);
    }

    /// <summary>字体来源与 winetricks 来源必须恰好一个成立：混用（下载字段 + verb）报错。</summary>
    [Fact]
    public void Parse_MixedDownloadAndWinetricksSources_Rejected()
    {
        const string json = """
            [
              { "id": "mixed", "version": "1", "winetricksVerb": "msftedit",
                "downloadUrl": "https://a/x.zip", "fileName": "x.zip",
                "md5": "ce3fc169af61a4e7bc650e7e78186330", "sizeBytes": 5,
                "dlls": [ { "arch": "x64", "fileName": "a.dll", "sizeBytes": 5,
                  "sha256": "e15ed4fefc3010c213694331ddfdc03767682325c898d773ab243e2dc8b08461" } ],
                "dllOverrides": { "*a": "native,builtin" } }
            ]
            """;

        var ex = Assert.Throws<DependencyCatalogException>(() => DependencyCatalog.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("混用"));
    }

    /// <summary>dlls/dllOverrides 只允许与 winetricksVerb 同时出现：纯下载来源带 dlls 报错。</summary>
    [Fact]
    public void Parse_DownloadManifestWithDlls_Rejected()
    {
        const string json = """
            [
              { "id": "font-with-dlls", "version": "1",
                "downloadUrl": "https://a/x.zip", "fileName": "x.zip",
                "md5": "ce3fc169af61a4e7bc650e7e78186330", "sizeBytes": 5,
                "dlls": [ { "arch": "x64", "fileName": "a.dll", "sizeBytes": 5,
                  "sha256": "e15ed4fefc3010c213694331ddfdc03767682325c898d773ab243e2dc8b08461" } ],
                "fonts": [ { "file": "a.ttc", "families": ["F"] } ], "replacementGroups": [] }
            ]
            """;

        var ex = Assert.Throws<DependencyCatalogException>(() => DependencyCatalog.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("dlls"));
    }

    /// <summary>字体来源携带空 dlls/dllOverrides 也算越界字段（字段只允许 winetricks 来源出现）。</summary>
    [Fact]
    public void Parse_DownloadManifestWithEmptyDllFields_Rejected()
    {
        const string json = """
            [
              { "id": "font-empty-dlls", "version": "1",
                "downloadUrl": "https://a/x.zip", "fileName": "x.zip",
                "md5": "ce3fc169af61a4e7bc650e7e78186330", "sizeBytes": 5,
                "dlls": [], "dllOverrides": {},
                "fonts": [ { "file": "a.ttc", "families": ["F"] } ], "replacementGroups": [] }
            ]
            """;

        var ex = Assert.Throws<DependencyCatalogException>(() => DependencyCatalog.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("dlls 只允许"));
        Assert.Contains(ex.Errors, e => e.Contains("dllOverrides 只允许"));
    }

    /// <summary>winetricksVerb 是 argv 一段：只允许小写字母/数字/._-，空白与斜杠必须拒绝。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("msftedit --force")]
    [InlineData("../msftedit")]
    [InlineData("MSFTEDIT")]
    [InlineData("-msftedit")]
    public void Parse_InvalidWinetricksVerb_Rejected(string verb)
    {
        var json = $$"""
            [
              { "id": "bad-verb", "version": "1", "winetricksVerb": "{{verb}}",
                "dlls": [ { "arch": "x64", "fileName": "a.dll", "sizeBytes": 5,
                  "sha256": "e15ed4fefc3010c213694331ddfdc03767682325c898d773ab243e2dc8b08461" } ],
                "dllOverrides": { "*a": "native,builtin" } }
            ]
            """;

        var ex = Assert.Throws<DependencyCatalogException>(() => DependencyCatalog.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("winetricksVerb"));
    }

    /// <summary>dlls 字段规则：arch 仅 x86/x64 且不重复、fileName 纯文件名、sha256 64 位小写 hex、size&gt;0。</summary>
    [Fact]
    public void Parse_InvalidDllEntries_AggregatesErrors()
    {
        const string json = """
            [
              { "id": "bad-dlls", "version": "1", "winetricksVerb": "msftedit",
                "dlls": [
                  { "arch": "arm64", "fileName": "../evil.dll", "sizeBytes": 0, "sha256": "ABC" },
                  { "arch": "x64", "fileName": "b.dll", "sizeBytes": 5,
                    "sha256": "e15ed4fefc3010c213694331ddfdc03767682325c898d773ab243e2dc8b08461" },
                  { "arch": "x64", "fileName": "c.dll", "sizeBytes": 5,
                    "sha256": "04ed66befdb822181ebd1d84cbf0b17aaadf8455ae742f44d7adcb26ab07bdad" }
                ],
                "dllOverrides": { "*a": "native,builtin" } }
            ]
            """;

        var ex = Assert.Throws<DependencyCatalogException>(() => DependencyCatalog.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("dlls[].arch"));
        Assert.Contains(ex.Errors, e => e.Contains("dlls[].fileName"));
        Assert.Contains(ex.Errors, e => e.Contains("dlls[].sha256"));
        Assert.Contains(ex.Errors, e => e.Contains("dlls[].sizeBytes"));
        Assert.Contains(ex.Errors, e => e.Contains("dlls[].arch") && e.Contains("重复"));
    }

    /// <summary>dllOverrides 字段规则：key 禁空格/路径分隔符/等号/控制字符，value 仅 native/builtin/disabled 逗号组合。</summary>
    [Fact]
    public void Parse_InvalidDllOverrides_AggregatesErrors()
    {
        const string json = """
            [
              { "id": "bad-overrides", "version": "1", "winetricksVerb": "msftedit",
                "dlls": [ { "arch": "x64", "fileName": "a.dll", "sizeBytes": 5,
                  "sha256": "e15ed4fefc3010c213694331ddfdc03767682325c898d773ab243e2dc8b08461" } ],
                "dllOverrides": {
                  "": "native",
                  "bad key": "native",
                  "path/key": "native",
                  "eq=key": "native",
                  "*ok": "native,evil",
                  "*empty": ""
                } }
            ]
            """;

        var ex = Assert.Throws<DependencyCatalogException>(() => DependencyCatalog.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("dllOverrides") && e.Contains("key"));
        Assert.Contains(ex.Errors, e => e.Contains("dllOverrides") && e.Contains("value"));
    }

    /// <summary>多个 dllOverrides 条目必须全部保留（备份/写入/恢复遍历整张表，不硬编码 *msftedit）。</summary>
    [Fact]
    public void Parse_MultipleDllOverrides_Preserved()
    {
        const string json = """
            [
              { "id": "multi-override", "version": "1", "winetricksVerb": "msftedit",
                "dlls": [ { "arch": "x64", "fileName": "a.dll", "sizeBytes": 5,
                  "sha256": "e15ed4fefc3010c213694331ddfdc03767682325c898d773ab243e2dc8b08461" } ],
                "dllOverrides": { "*msftedit": "native,builtin", "msftedit": "native" } }
            ]
            """;

        var manifest = Assert.Single(DependencyCatalog.Parse(json));

        Assert.Equal(2, manifest.DllOverrides!.Count);
        Assert.Equal("native", manifest.DllOverrides["msftedit"]);
    }

    /// <summary>winetricks 来源缺 dlls / dllOverrides，或字体来源缺 fonts/replacementGroups，都必须报错。</summary>
    [Fact]
    public void Parse_SourceSpecificRequiredFields_Enforced()
    {
        const string json = """
            [
              { "id": "no-dlls", "version": "1", "winetricksVerb": "msftedit",
                "dllOverrides": { "*a": "native" } },
              { "id": "no-overrides", "version": "1", "winetricksVerb": "msftedit",
                "dlls": [ { "arch": "x64", "fileName": "a.dll", "sizeBytes": 5,
                  "sha256": "e15ed4fefc3010c213694331ddfdc03767682325c898d773ab243e2dc8b08461" } ] },
              { "id": "font-null-groups", "version": "1", "downloadUrl": "https://a/x.zip",
                "fileName": "x.zip", "md5": "ce3fc169af61a4e7bc650e7e78186330", "sizeBytes": 5,
                "fonts": [ { "file": "a.ttc", "families": ["F"] } ], "replacementGroups": null }
            ]
            """;

        var ex = Assert.Throws<DependencyCatalogException>(() => DependencyCatalog.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("no-dlls") && e.Contains("dlls"));
        Assert.Contains(ex.Errors, e => e.Contains("no-overrides") && e.Contains("dllOverrides"));
        Assert.Contains(ex.Errors, e => e.Contains("font-null-groups") && e.Contains("replacementGroups"));
    }

    /// <summary>内嵌清单同时含字体来源与 winetricks 来源；后者字段与真机实测一致。</summary>
    [Fact]
    public void LoadEmbedded_ContainsRichEditMsfteditManifest()
    {
        var manifests = DependencyCatalog.LoadEmbedded();

        var richEdit = Assert.Single(manifests, m => m.Id == "richedit-msftedit");
        Assert.Equal("6.1.7601.17514", richEdit.Version);
        Assert.Equal("msftedit", richEdit.WinetricksVerb);
        Assert.Null(richEdit.Fonts);
        Assert.Null(richEdit.ReplacementGroups);
        Assert.Equal(2, richEdit.Dlls!.Count);
        Assert.Equal(
            "e15ed4fefc3010c213694331ddfdc03767682325c898d773ab243e2dc8b08461",
            Assert.Single(richEdit.Dlls, d => d.Arch == "x64").Sha256);
        Assert.Equal(
            "04ed66befdb822181ebd1d84cbf0b17aaadf8455ae742f44d7adcb26ab07bdad",
            Assert.Single(richEdit.Dlls, d => d.Arch == "x86").Sha256);
        Assert.Equal("native,builtin", richEdit.DllOverrides!["*msftedit"]);
    }

    /// <summary>内嵌 cjk-fonts 清单自检：资源在、过校验、Bottles 4 组 + YAGL KRSDK H7GBK 别名组
    /// （28 族名 + 5 替换组 10/6/7/18/16；SC 目标两组：系统字体名 + KRSDK H7GBK 别名）。</summary>
    [Fact]
    public void LoadEmbedded_CjkFonts_MatchesResearchedManifestShape()
    {
        var manifests = DependencyCatalog.LoadEmbedded();

        var cjk = Assert.Single(manifests, m => m.Id == "cjk-fonts");
        Assert.Equal("2.004R-1", cjk.Version);
        Assert.StartsWith("https://github.com/adobe-fonts/source-han-sans/releases/", cjk.DownloadUrl);
        Assert.Equal("SourceHanSans.ttc.zip", cjk.FileName);
        Assert.Equal(32, cjk.Md5!.Length);
        Assert.True(cjk.SizeBytes > 0);

        var font = Assert.Single(cjk.Fonts!);
        Assert.Equal("SourceHanSans.ttc", font.File);
        Assert.Equal(28, font.Families.Count);
        Assert.Contains("Source Han Sans SC ExtraLight", font.Families);
        Assert.Contains("Source Han Sans K Heavy", font.Families);

        Assert.Equal(5, cjk.ReplacementGroups!.Count);
        Assert.Equal(57, cjk.ReplacementGroups.Sum(g => g.Replaces.Count));
        var sc = Assert.Single(cjk.ReplacementGroups, g => g.Replaces.Contains("Microsoft YaHei"));
        Assert.Equal("Source Han Sans SC", sc.Target);
        Assert.Equal(10, sc.Replaces.Count);
        Assert.Contains("SimSun-ExtB", sc.Replaces);
        var h7 = Assert.Single(cjk.ReplacementGroups, g => g.Replaces.Contains("文鼎方新书H7GBK_H"));
        Assert.Equal("Source Han Sans SC", h7.Target);
        Assert.Equal(
            [
                "文鼎方新书H7GBK_H", "文鼎方新书H7GBK", "AR FangXinShuH7GBK",
                "AR FangXinShuH7GBK Heavy", "H7GBKHeavy", "Source Han Sans CN Regular",
            ],
            h7.Replaces);
        var tc = Assert.Single(cjk.ReplacementGroups, g => g.Target == "Source Han Sans TC");
        Assert.Equal(7, tc.Replaces.Count);
        Assert.Contains("PMingLiU", tc.Replaces);
        var jp = Assert.Single(cjk.ReplacementGroups, g => g.Target == "Source Han Sans");
        Assert.Equal(18, jp.Replaces.Count);
        Assert.Contains("MS Gothic", jp.Replaces);
        Assert.Contains("メイリオ", jp.Replaces);
        Assert.Contains("ＭＳ 明朝", jp.Replaces);
        var kr = Assert.Single(cjk.ReplacementGroups, g => g.Target == "Source Han Sans K");
        Assert.Equal(16, kr.Replaces.Count);
        Assert.Contains("Malgun Gothic", kr.Replaces);
        Assert.Contains("바탕", kr.Replaces);
    }
}
