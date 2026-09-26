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
            "archiveEntry": "SourceHanSans.ttc",
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
        Assert.Equal("SourceHanSans.ttc", manifest.ArchiveEntry);
        var font = Assert.Single(manifest.Fonts);
        Assert.Equal("SourceHanSans.ttc", font.File);
        Assert.Equal(["Source Han Sans SC", "Source Han Sans TC"], font.Families);
        var group = Assert.Single(manifest.ReplacementGroups);
        Assert.Equal("Source Han Sans SC", group.Target);
        Assert.Equal(["SimSun", "SimHei"], group.Replaces);
    }

    [Fact]
    public void Parse_AggregatesAllValidationErrors()
    {
        const string json = """
            [
              { "id": "Bad_Id", "version": "", "downloadUrl": "http://a.example/x.zip", "fileName": "",
                "md5": "xyz", "sizeBytes": 0, "archiveEntry": "../evil.ttc", "fonts": [], "replacementGroups": [] },
              { "id": "second", "version": "1", "downloadUrl": "https://a.example/x.zip", "fileName": "x.zip",
                "md5": "ce3fc169af61a4e7bc650e7e78186330", "sizeBytes": 5, "archiveEntry": "a.ttc",
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
        Assert.Contains(ex.Errors, e => e.Contains("archiveEntry"));
        Assert.Contains(ex.Errors, e => e.Contains("fonts"));
        Assert.Contains(ex.Errors, e => e.Contains("replacementGroups"));
    }

    [Fact]
    public void Parse_DuplicateId_Fails()
    {
        const string json = """
            [
              { "id": "a", "version": "1", "downloadUrl": "https://a/x.zip", "fileName": "x.zip",
                "md5": "ce3fc169af61a4e7bc650e7e78186330", "sizeBytes": 5, "archiveEntry": "a.ttc",
                "fonts": [ { "file": "a.ttc", "families": ["F"] } ], "replacementGroups": [] },
              { "id": "A", "version": "2", "downloadUrl": "https://b/x.zip", "fileName": "y.zip",
                "md5": "ce3fc169af61a4e7bc650e7e78186330", "sizeBytes": 5, "archiveEntry": "a.ttc",
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

    /// <summary>内嵌 cjk-fonts 清单自检：资源在、过校验、内容与 Bottles cjkfonts.yml 一致
    /// （28 族名 + 4 替换组 10/7/18/16，含 CJK 原名条目）。</summary>
    [Fact]
    public void LoadEmbedded_CjkFonts_MatchesResearchedManifestShape()
    {
        var manifests = DependencyCatalog.LoadEmbedded();

        var cjk = Assert.Single(manifests, m => m.Id == "cjk-fonts");
        Assert.Equal("2.004R", cjk.Version);
        Assert.StartsWith("https://github.com/adobe-fonts/source-han-sans/releases/", cjk.DownloadUrl);
        Assert.Equal("SourceHanSans.ttc.zip", cjk.FileName);
        Assert.Equal(32, cjk.Md5.Length);
        Assert.True(cjk.SizeBytes > 0);
        Assert.Equal("SourceHanSans.ttc", cjk.ArchiveEntry);

        var font = Assert.Single(cjk.Fonts);
        Assert.Equal("SourceHanSans.ttc", font.File);
        Assert.Equal(28, font.Families.Count);
        Assert.Contains("Source Han Sans SC ExtraLight", font.Families);
        Assert.Contains("Source Han Sans K Heavy", font.Families);

        Assert.Equal(4, cjk.ReplacementGroups.Count);
        var sc = Assert.Single(cjk.ReplacementGroups, g => g.Target == "Source Han Sans SC");
        Assert.Equal(10, sc.Replaces.Count);
        Assert.Contains("Microsoft YaHei", sc.Replaces);
        Assert.Contains("SimSun-ExtB", sc.Replaces);
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
