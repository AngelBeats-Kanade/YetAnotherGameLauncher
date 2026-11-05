using System.Text;
using Xunit;
using YetAnotherGameLauncher.Core.Dependencies;

namespace YetAnotherGameLauncher.Core.Tests.Dependencies;

/// <summary>
/// .reg 脚本生成：节顺序、键值格式、值转义、空节省略——断言对照完整期望文本，防死分支。
/// </summary>
public class WineRegistryScriptBuilderTests
{
    private static DependencyManifest Manifest(
        IReadOnlyList<DependencyFont>? fonts = null,
        IReadOnlyList<DependencyReplacementGroup>? groups = null) =>
        new(
            "cjk-fonts", "2.004R",
            "https://example.com/x.zip", "x.zip",
            "ce3fc169af61a4e7bc650e7e78186330", 1,
            fonts ?? [new DependencyFont("SourceHanSans.ttc", ["Source Han Sans SC", "Source Han Sans TC"])],
            groups ?? [new DependencyReplacementGroup("Source Han Sans SC", ["SimSun", "Microsoft YaHei"])]);

    [Fact]
    public void Build_EmitsFontsSectionThenReplacementsSection()
    {
        var script = WineRegistryScriptBuilder.Build(Manifest());

        var expected =
            "Windows Registry Editor Version 5.00\n" +
            "\n" +
            "[HKEY_LOCAL_MACHINE\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Fonts]\n" +
            "\"Source Han Sans SC\"=\"SourceHanSans.ttc\"\n" +
            "\"Source Han Sans TC\"=\"SourceHanSans.ttc\"\n" +
            "\n" +
            "[HKEY_CURRENT_USER\\Software\\Wine\\Fonts\\Replacements]\n" +
            "\"SimSun\"=\"Source Han Sans SC\"\n" +
            "\"Microsoft YaHei\"=\"Source Han Sans SC\"\n";
        Assert.Equal(expected, script);
    }

    [Fact]
    public void Build_EscapesBackslashAndQuoteInValues()
    {
        var script = WineRegistryScriptBuilder.Build(Manifest(
            fonts: [new DependencyFont("a\\b.ttc", ["Weird \"Name\""])]));
        Assert.Contains("\"Weird \\\"Name\\\"\"=\"a\\\\b.ttc\"", script);
    }

    [Fact]
    public void Build_EmptySections_Omitted()
    {
        var fontsOnly = WineRegistryScriptBuilder.Build(Manifest(groups: []));
        Assert.DoesNotContain("Replacements", fontsOnly);

        var groupsOnly = WineRegistryScriptBuilder.Build(Manifest(fonts: []));
        Assert.DoesNotContain("CurrentVersion\\Fonts]", groupsOnly);
        Assert.Contains("[HKEY_CURRENT_USER\\Software\\Wine\\Fonts\\Replacements]", groupsOnly);
    }

    [Fact]
    public void Build_EmbeddedCjkFonts_EmitsAllReplacementMappingsIncludingSdkAliases()
    {
        var cjk = Assert.Single(DependencyCatalog.LoadEmbedded(), m => m.Id == "cjk-fonts");
        var script = WineRegistryScriptBuilder.Build(cjk);

        var marker = "[HKEY_CURRENT_USER\\Software\\Wine\\Fonts\\Replacements]\n";
        var index = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "缺少 Replacements 节");
        var lines = script[(index + marker.Length)..]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(line => !line.StartsWith('['))
            .ToArray();
        var expectedCount = cjk.ReplacementGroups!.Sum(g => g.Replaces.Count);

        // 既有系统字体名 + SDK H7GBK 别名组共用同一 target：在单次 .reg 中合并输出。
        Assert.Equal(57, expectedCount);
        Assert.Equal(expectedCount, lines.Length);
        Assert.Contains("\"SimSun\"=\"Source Han Sans SC\"", lines);
        foreach (var alias in new[]
                 {
                     "文鼎方新书H7GBK_H", "文鼎方新书H7GBK", "AR FangXinShuH7GBK",
                     "AR FangXinShuH7GBK Heavy", "H7GBKHeavy", "Source Han Sans CN Regular",
                 })
        {
            Assert.Contains($"\"{alias}\"=\"Source Han Sans SC\"", lines);
        }
    }

    /// <summary>内嵌清单经 WriteAsync 落成 UTF-16LE BOM 后再按 UTF-16LE 回读：H7 中文别名必须逐字
    /// 往返（内存字符串断言不能发现编码层把非 ASCII 写坏）。</summary>
    [Fact]
    public async Task WriteAsync_EmbeddedCjkFonts_RoundTripsH7AliasesAsUtf16LeBom()
    {
        var cjk = Assert.Single(DependencyCatalog.LoadEmbedded(), m => m.Id == "cjk-fonts");
        var script = WineRegistryScriptBuilder.Build(cjk);
        var path = Path.Combine(Path.GetTempPath(), $"yagl-cjk-{Guid.NewGuid():N}.reg");
        try
        {
            await WineRegistryScriptBuilder.WriteAsync(path, script);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(0xFF, bytes[0]);
            Assert.Equal(0xFE, bytes[1]);

            var text = Encoding.Unicode.GetString(bytes[2..]);
            Assert.Equal(script, text);
            Assert.Contains("\"文鼎方新书H7GBK_H\"=\"Source Han Sans SC\"", text);
            Assert.Contains("\"AR FangXinShuH7GBK Heavy\"=\"Source Han Sans SC\"", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>winetricks 来源不生成字体注册表脚本：误入 builder 必须显式失败，不能产出空 .reg
    /// （DLL 覆盖走逐条 <c>reg add</c>）。</summary>
    [Fact]
    public void Build_WinetricksManifest_ThrowsInsteadOfEmptyScript()
    {
        var manifest = new DependencyManifest(
            "richedit-msftedit", "6.1.7601.17514",
            null, null, null, null, null, null,
            WinetricksVerb: "msftedit",
            Dlls:
            [
                new DependencyDll(
                    "x64", "msftedit.dll", 799744,
                    "e15ed4fefc3010c213694331ddfdc03767682325c898d773ab243e2dc8b08461"),
            ],
            DllOverrides: new Dictionary<string, string> { ["*msftedit"] = "native,builtin" });

        Assert.Throws<InvalidOperationException>(() => WineRegistryScriptBuilder.Build(manifest));
    }

    [Fact]
    public void Build_MultipleGroups_KeepOrder()
    {
        var script = WineRegistryScriptBuilder.Build(Manifest(
            groups:
            [
                new DependencyReplacementGroup("Target A", ["Old A"]),
                new DependencyReplacementGroup("Target B", ["Old B1", "Old B2"]),
            ]));

        var expected =
            "[HKEY_CURRENT_USER\\Software\\Wine\\Fonts\\Replacements]\n" +
            "\"Old A\"=\"Target A\"\n" +
            "\"Old B1\"=\"Target B\"\n" +
            "\"Old B2\"=\"Target B\"\n";
        Assert.EndsWith(expected, script);
    }
}
