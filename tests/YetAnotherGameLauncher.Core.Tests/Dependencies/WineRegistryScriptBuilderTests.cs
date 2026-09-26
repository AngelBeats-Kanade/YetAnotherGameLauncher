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
            "ce3fc169af61a4e7bc650e7e78186330", 1, "a.ttc",
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
