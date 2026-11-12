using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

/// <summary>
/// 档位资源包下载（2026-10-19）：官方档位索引 resourcePacks 的裁剪/追加语义——
/// sd/uhd 全量清单 = 标准清单裁掉其它档位目录 + 追加目标档位包；hd/未设置 = 标准清单不变
/// （官方档位索引的 hd 包与标准清单 HD 条目逐字节一致，2026-10-19 实测，不额外联网）。
/// </summary>
public class KuroResourcePackTests
{
    private const string Cdn = "https://cdn-pack.example.com/";
    private const string TierIndexUrl = "https://prod-cn.example.com/launcher/game/G152/10003_token/tiered/index.json";
    private const string TierCdn = "https://cdn-tier.example.com/";

    private readonly FakeDownloader _downloader = new();

    private KuroChannelApi CreateApi() => new(_downloader);

    [Theory]
    [InlineData("3.7.1", "3.7.1", true)]
    [InlineData("3.7", "3.7.0", true)]
    [InlineData("3.7.1.0", "3.7.1", true)]
    [InlineData("3.7.0", "3.7.1", false)]
    [InlineData("3.7.1-beta", "3.7.1", false)]
    [InlineData("", "3.7.1", false)]
    public void VersionsMatch_NormalizesDottedNumericVersions(string left, string right, bool expected) =>
        Assert.Equal(expected, KuroChannelApi.VersionsMatch(left, right));

    /// <summary>带档位索引选项的国服服务器。</summary>
    private static GameServer Server() => new()
    {
        Id = "cn",
        Name = "国服",
        Options = new Dictionary<string, string>
        {
            ["indexUrl"] = "https://prod-cn.example.com/launcher/game/G152/10003_token/index.json",
            ["resourcePackIndexUrl"] = TierIndexUrl,
        },
    };

    /// <summary>不带档位索引的服务器（国际服/B 服实测 404：档位只作启动参数）。</summary>
    private static GameServer ServerWithoutTierIndex() => new()
    {
        Id = "global",
        Name = "国际服",
        Options = new Dictionary<string, string>
        {
            ["indexUrl"] = "https://prod-global.example.com/launcher/game/G153/10003_token/index.json",
        },
    };

    private static string Md5(string s) => Hashing.Md5Hex(System.Text.Encoding.UTF8.GetBytes(s));

    /// <summary>标准 index.json + 标准全量清单（common + 100 条式样的 HD 条目）。
    /// indexFileJson 可传入畸形清单覆盖默认 fixture（边界校验用）。</summary>
    private void RegisterStandardFixture(
        string indexUrl = "https://prod-cn.example.com/launcher/game/G152/10003_token/index.json",
        string version = "3.7.1",
        string? indexFileJson = null)
    {
        indexFileJson ??= """
            {
              "resource": [
                { "dest": "Client/Binaries/Win64/Client-Win64-Shipping.exe", "md5": "aaaa1111aaaa1111aaaa1111aaaa1111", "size": 10 },
                { "dest": "Client/Content/HD/pakchunk1-HD-WindowsNoEditor.pak", "md5": "bbbb2222bbbb2222bbbb2222bbbb2222", "size": 20 },
                { "dest": "Client/Content/HD/pakchunk1-HD-WindowsNoEditor.sig", "md5": "cccc3333cccc3333cccc3333cccc3333", "size": 5 }
              ]
            }
            """;

        var indexJson = $$"""
            {
              "default": {
                "version": "{{version}}",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/{{version}}/token/zip/",
                "config": {
                  "version": "{{version}}",
                  "indexFile": "resource/10003/{{version}}/token/indexFile.json",
                  "indexFileMd5": "{{Md5(indexFileJson)}}",
                  "baseUrl": "launcher/game/G152/10003/{{version}}/token/zip/"
                }
              }
            }
            """;

        _downloader.Serve(indexUrl, indexJson);
        _downloader.Serve(Cdn + $"resource/10003/{version}/token/indexFile.json", indexFileJson);
    }

    /// <summary>档位索引 + 指定档位的包清单，注册到假下载器。packVersion 可独立于路径版本，
    /// 用于构造"包版本滞后于游戏版本"的窗口期形态。</summary>
    private void RegisterTierFixture(
        string tier, string packIndexFileJson, string version = "3.7.1", string? packVersion = null)
    {
        var resolvedPackVersion = packVersion ?? version;
        var tierIndexJson = $$"""
            {
              "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{TierCdn}}" } ],
              "config": { "version": "{{version}}" },
              "resourcePacks": {
                "{{tier}}": {
                  "version": "{{resolvedPackVersion}}",
                  "indexFile": "launcher/game/G152/10003/{{version}}/token/{{tier}}/indexFile.json",
                  "indexFileMd5": "{{Md5(packIndexFileJson)}}",
                  "baseUrl": "launcher/game/G152/10003/{{version}}/token/zip/",
                  "size": 123
                }
              }
            }
            """;
        _downloader.Serve(TierIndexUrl, tierIndexJson);
        _downloader.Serve(
            TierCdn + $"launcher/game/G152/10003/{version}/token/{tier}/indexFile.json", packIndexFileJson);
    }

    private static string PackIndexFileJson(string tierDir, params (string Name, long Size)[] files) =>
        $$"""
          {
            "resource": [
            {{string.Join(",\n", files.Select((f, i) =>
                $"    {{ \"dest\": \"Client/Content/{tierDir}/{f.Name}\", \"md5\": \"{i:D4}0000{i:D4}0000{i:D4}0000{i:D4}0000\", \"size\": {f.Size} }}"))}}
            ]
          }
          """;

    // ---------- 全量清单：sd / uhd 裁剪其它档位 + 追加目标档位包 ----------

    [Theory]
    [InlineData("sd", "SD")]
    [InlineData("uhd", "UHD")]
    public async Task GetManifest_TieredTier_CropsOtherTiersAndAppendsSelectedPack(string tier, string dir)
    {
        RegisterStandardFixture();
        RegisterTierFixture(tier, PackIndexFileJson(dir, ("pakchunk1-X-WindowsNoEditor.pak", 100), ("pakchunk1-X-WindowsNoEditor.sig", 7)));

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.7.1", tier);

        // 选中档位包追加 + 标准清单的 HD 条目（未选中档位）裁掉；common 条目保留
        Assert.Equal(
            ["Client/Binaries/Win64/Client-Win64-Shipping.exe",
             $"Client/Content/{dir}/pakchunk1-X-WindowsNoEditor.pak",
             $"Client/Content/{dir}/pakchunk1-X-WindowsNoEditor.sig"],
            [.. manifest.Files.Select(f => f.Path)]);
        Assert.Equal(tier, manifest.ResourceTier);
        // 未选中档位目录必须保留（切档不隐式删旧档；清理只走显式动作）
        Assert.Equal(
            ResourceTiers.OtherTiers(tier).Select(t => $"Client/Content/{t.ToUpperInvariant()}").OrderBy(p => p),
            manifest.PreservedPaths.OrderBy(p => p));
        // 档位包条目 URL = 档位索引 CDN + 包 baseUrl + dest
        var packFile = manifest.Files.First(f => f.Path.Contains($"/{dir}/"));
        Assert.Equal($"{TierCdn}launcher/game/G152/10003/3.7.1/token/zip/{packFile.Path}", packFile.Url);
    }

    [Fact]
    public async Task GetManifest_TieredTier_PackFilesCarrySizeAndMd5()
    {
        RegisterStandardFixture();
        RegisterTierFixture("uhd", PackIndexFileJson("UHD", ("pakchunk9-UHD-WindowsNoEditor.pak", 4242)));

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.7.1", "uhd");

        var packFile = manifest.Files.Single(f => f.Path.StartsWith("Client/Content/UHD/", StringComparison.Ordinal));
        Assert.Equal(4242, packFile.Size);
        Assert.Equal("00000000000000000000000000000000", packFile.Md5);
    }

    [Fact]
    public async Task GetManifest_TierPackVersionMismatch_ThrowsBeforeFetchingPackFiles()
    {
        // 版本窗口期：游戏 base 已到 3.7.1，档位包仍是 3.7.0。混用两版内容会让后续
        // 修复轮/游戏挂载对不上；硬失败并提示稍后重试或暂切 HD，不静默返回错版清单。
        RegisterStandardFixture(version: "3.7.1");
        RegisterTierFixture("uhd", PackIndexFileJson("UHD", ("pakchunk1-UHD-WindowsNoEditor.pak", 100)),
            packVersion: "3.7.0");

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "uhd"));

        Assert.Contains("3.7.0", ex.Message);
        Assert.Contains("3.7.1", ex.Message);
        Assert.DoesNotContain(
            TierCdn + "launcher/game/G152/10003/3.7.1/token/uhd/indexFile.json",
            _downloader.Requests);
    }

    [Fact]
    public async Task GetManifest_TierPackVersionEquivalent_AllowsSync()
    {
        // 归一化：3.7.1.0 与 3.7.1 是同一版本，不得误杀
        RegisterStandardFixture(version: "3.7.1");
        RegisterTierFixture("uhd", PackIndexFileJson("UHD", ("pakchunk1-UHD-WindowsNoEditor.pak", 100)),
            packVersion: "3.7.1.0");

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.7.1", "uhd");

        Assert.Contains(manifest.Files,
            f => f.Path == "Client/Content/UHD/pakchunk1-UHD-WindowsNoEditor.pak");
    }

    [Fact]
    public async Task GetManifest_TierPackVersionMissing_Throws()
    {
        // 无法确认版本时同样不能放行：宁可让用户稍后重试，也不返回来源不明的档位内容
        RegisterStandardFixture(version: "3.7.1");
        RegisterTierFixture("uhd", PackIndexFileJson("UHD", ("pakchunk1-UHD-WindowsNoEditor.pak", 100)),
            packVersion: "");

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "uhd"));

        Assert.Contains("no version", ex.Message);
    }

    // ---------- 清单边界（2026-10-23）：官方清单是不可信输入 ----------

    [Fact]
    public async Task GetManifest_EntryUnderReservedPath_Throws()
    {
        // .yagl 是启动器状态目录：清单若把它当游戏文件写入，会覆盖下载会话/本地版本记录
        RegisterStandardFixture(indexFileJson: """
            {
              "resource": [
                { "dest": ".yagl/state.json", "md5": "aaaa1111aaaa1111aaaa1111aaaa1111", "size": 10 }
              ]
            }
            """);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "hd"));

        Assert.Contains(".yagl", ex.Message);
    }

    [Fact]
    public async Task GetManifest_DuplicatePaths_DedupesFirstWins()
    {
        // 重复 dest 先登记者胜（与 BuildResourceByDest 同一决定：清单不可信，F42 同族——冗余条目
        // 不得炸清单获取）；去重同时消除"两个条目并发写同一目标"的竞态（2026-10-26 复审）。
        RegisterStandardFixture(indexFileJson: """
            {
              "resource": [
                { "dest": "Client/Content/HD/same.pak", "md5": "aaaa1111aaaa1111aaaa1111aaaa1111", "size": 10 },
                { "dest": "Client/Content/HD/same.pak", "md5": "bbbb2222bbbb2222bbbb2222bbbb2222", "size": 20 }
              ]
            }
            """);

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.7.1", "hd");

        var entry = Assert.Single(manifest.Files);
        Assert.Equal(10, entry.Size); // 先登记者胜，不是后到者
        Assert.Equal("aaaa1111aaaa1111aaaa1111aaaa1111", entry.Md5);
    }

    [Fact]
    public async Task GetManifest_WhitespaceMd5_Throws()
    {
        // 空白 MD5 不算"未提供校验信息"：下游（ManifestVerifier.CheckFile 的 IsNullOrEmpty 口径）
        // 会把它当"已提供但比不上"而永久失败，故按畸形值拒收（2026-10-26 复审）。
        RegisterStandardFixture(indexFileJson: """
            {
              "resource": [
                { "dest": "Client/Content/HD/bad.pak", "md5": " ", "size": 10 }
              ]
            }
            """);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "hd"));

        Assert.Contains("invalid MD5", ex.Message);
    }

    [Fact]
    public async Task GetManifest_FileDirectoryConflict_Throws()
    {
        RegisterStandardFixture(indexFileJson: """
            {
              "resource": [
                { "dest": "Client/Content/HD/pak", "md5": "aaaa1111aaaa1111aaaa1111aaaa1111", "size": 10 },
                { "dest": "Client/Content/HD/pak/inner.pak", "md5": "bbbb2222bbbb2222bbbb2222bbbb2222", "size": 20 }
              ]
            }
            """);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "hd"));

        Assert.Contains("conflicts", ex.Message);
    }

    [Fact]
    public async Task GetManifest_InvalidMd5_Throws()
    {
        RegisterStandardFixture(indexFileJson: """
            {
              "resource": [
                { "dest": "Client/Content/HD/bad.pak", "md5": "not-an-md5", "size": 10 }
              ]
            }
            """);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "hd"));

        Assert.Contains("invalid MD5", ex.Message);
    }

    [Fact]
    public async Task GetManifest_EmptyMd5_ToleratedAsUnprovided()
    {
        // 空 MD5 = 渠道未提供校验信息（ManifestVerifier.CheckFile 同语义：跳过该项而非恒判损坏），
        // 不属"畸形值"，不得误拒——否则带无校验条目的真实清单会被整体拒收。
        RegisterStandardFixture(indexFileJson: """
            {
              "resource": [
                { "dest": "Client/Content/HD/ok.pak", "md5": "", "size": 10 }
              ]
            }
            """);

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.7.1", "hd");

        Assert.Equal(["Client/Content/HD/ok.pak"], [.. manifest.Files.Select(f => f.Path)]);
    }

    [Fact]
    public async Task GetManifest_TraversalPath_Throws()
    {
        RegisterStandardFixture(indexFileJson: """
            {
              "resource": [
                { "dest": "../outside.pak", "md5": "aaaa1111aaaa1111aaaa1111aaaa1111", "size": 10 }
              ]
            }
            """);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "hd"));

        Assert.Contains("unsafe", ex.Message);
    }

    [Theory]
    [InlineData("Saved./slot.sav")]
    [InlineData("Saved /slot.sav")]
    [InlineData("Client/Content/HD/pak.")]
    public async Task GetManifest_TrailingDotOrSpaceSegment_Throws(string dest)
    {
        // Windows 建文件时吃掉每段的尾点/尾空格（`Saved.` 实际落进 `Saved/`、`pak.` → `pak`）：
        // 词法比较会漏判保留路径（Saved./x 绕过"保留路径一律拒绝"）与重复路径（x/pak 与 x/pak.
        // 并发写同一文件）。此类命名跨平台不可移植，直接拒收而非静默改名（2026-10-23 复审）。
        RegisterStandardFixture(indexFileJson: $$"""
            {
              "resource": [
                { "dest": "{{dest}}", "md5": "aaaa1111aaaa1111aaaa1111aaaa1111", "size": 10 }
              ]
            }
            """);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "hd"));

        Assert.Contains("trailing dot/space", ex.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"resource\": []}")]
    [InlineData("{\"resource\": null}")]
    [InlineData("{\"resource\": [ {\"dest\": \"  \", \"md5\": \"aaaa1111aaaa1111aaaa1111aaaa1111\", \"size\": 1} ]}")]
    public async Task GetManifest_EmptyOrNullBase_RejectsInsteadOfReturningEmptyManifest(string indexFileJson)
    {
        // P1（2026-10-26 复审）：空/空白全量清单会让同步收尾把已装内容全当游离文件删掉（几十 GB）。
        // `{}`/空数组/null/全空白 dest 都归一为"没有可安装文件"，必须拒收而不是返回空清单。
        RegisterStandardFixture(indexFileJson: indexFileJson);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "hd"));

        Assert.Contains("lists no files", ex.Message);
    }

    [Fact]
    public async Task GetManifest_TieredEmptyBase_RejectsInsteadOfPackOnlyManifest()
    {
        // 非 HD：base 裁掉所有档位目录后为空 → 只剩档位包的清单同样会让清理删光 common，
        // 必须与"包清单空"同族拒收（2026-10-26 复审 P1）。
        RegisterStandardFixture(indexFileJson: "{\"resource\": []}");
        RegisterTierFixture("sd", PackIndexFileJson("SD", ("pak.pak", 100)));

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "sd"));

        Assert.Contains("common", ex.Message);
    }

    [Fact]
    public async Task GetManifest_BackslashTierEntry_CroppedLikeSlashForm()
    {
        // JSON 允许反斜杠分隔：base 里的 `Client\Content\SD\x.pak` 与包里的 `Client/Content/SD/x.pak`
        // 是同一目标。档位裁剪必须先归一 '\'→'/'，否则漏裁并与包条目叠成"双条目写同一文件"
        //（2026-10-26 复审 P3）。
        RegisterStandardFixture(indexFileJson: """
            {
              "resource": [
                { "dest": "Client/Binaries/Win64/Client-Win64-Shipping.exe", "md5": "aaaa1111aaaa1111aaaa1111aaaa1111", "size": 10 },
                { "dest": "Client\\Content\\SD\\x.pak", "md5": "bbbb2222bbbb2222bbbb2222bbbb2222", "size": 20 }
              ]
            }
            """);
        RegisterTierFixture("sd", PackIndexFileJson("SD", ("x.pak", 100)));

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.7.1", "sd");

        var entry = Assert.Single(
            manifest.Files, f => f.Path.EndsWith("x.pak", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(100, entry.Size); // 取包那份；base 的反斜杠条目已被裁掉
    }

    [Fact]
    public async Task GetIncrementalManifest_NullCollectionProperties_Coalesced()
    {
        // STJ 会把 "resource": null、"srcFiles": null 赋给非空注解属性（未开 RespectNullableAnnotations）：
        // 消费处必须 coalesce，否则 Where/ToManifestFiles 抛未分类异常而非 UpdateException
        //（2026-10-26 复审 P3）。增量清单允许空 Files + 非空 Groups，不能套空全量清单的拒收规则。
        var patchIndexFileJson = """
            {
              "resource": null,
              "groupInfos": [
                {
                  "dest": "patch_ok.krpdiff", "size": 128, "md5": "abababababababababababababababab",
                  "srcFiles": null, "dstFiles": null
                }
              ]
            }
            """;
        RegisterStandardFixture();
        var indexJson = $$"""
            {
              "default": {
                "version": "3.7.1",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.7.1/token/zip/",
                "config": {
                  "version": "3.7.1",
                  "indexFile": "resource/10003/3.7.1/token/indexFile.json",
                  "indexFileMd5": "{{Md5("ignored")}}",
                  "patchConfig": [
                    { "version": "3.7.0", "indexFile": "resource/10003/3.7.1/370/indexFile.json",
                      "indexFileMd5": "{{Md5(patchIndexFileJson)}}", "baseUrl": "launcher/game/G152/10003/3.7.1/patch370/" }
                  ]
                }
              }
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/10003/3.7.1/370/indexFile.json", patchIndexFileJson);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.7.0", "3.7.1", "hd");

        Assert.NotNull(manifest);
        Assert.Empty(manifest.Files);
        var group = Assert.Single(manifest.Groups);
        Assert.Empty(group.SrcFiles);
        Assert.Empty(group.DstFiles);
    }

    [Fact]
    public async Task GetIncrementalManifest_DeleteFilesTolerantShapes_Kept()
    {
        // 消费端本来就跳过目录条目、并按落地语义归一别名；在清单获取期拒收会让增量更新被永久挡住
        //（增量链没有整包回退），所以边界只拒绝对/逃逸路径（2026-10-26 复审 P2）。
        var patchIndexFileJson = """
            {
              "resource": [],
              "deleteFiles": [
                "Client/Content/Paks/Old/",
                "./Client/Content/Paks/old.pak",
                "data/../data/old2.pak",
                "a//b.pak"
              ]
            }
            """;
        RegisterStandardFixture();
        var indexJson = $$"""
            {
              "default": {
                "version": "3.7.1",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.7.1/token/zip/",
                "config": {
                  "version": "3.7.1",
                  "indexFile": "resource/10003/3.7.1/token/indexFile.json",
                  "indexFileMd5": "{{Md5("ignored")}}",
                  "patchConfig": [
                    { "version": "3.7.0", "indexFile": "resource/10003/3.7.1/370/indexFile.json",
                      "indexFileMd5": "{{Md5(patchIndexFileJson)}}", "baseUrl": "launcher/game/G152/10003/3.7.1/patch370/" }
                  ]
                }
              }
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/10003/3.7.1/370/indexFile.json", patchIndexFileJson);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.7.0", "3.7.1", "hd");

        Assert.NotNull(manifest);
        Assert.Equal(4, manifest.DeleteFiles.Count);
    }

    [Fact]
    public async Task GetIncrementalManifest_DeleteFilesEscapingRoot_Rejected()
    {
        // 删除清单可以宽容，但逃逸路径是明确的攻击面：仍在清单获取期拒收（2026-10-26 复审 P2）。
        var patchIndexFileJson = """
            {
              "resource": [],
              "deleteFiles": [ "../outside.pak" ]
            }
            """;
        RegisterStandardFixture();
        var indexJson = $$"""
            {
              "default": {
                "version": "3.7.1",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.7.1/token/zip/",
                "config": {
                  "version": "3.7.1",
                  "indexFile": "resource/10003/3.7.1/token/indexFile.json",
                  "indexFileMd5": "{{Md5("ignored")}}",
                  "patchConfig": [
                    { "version": "3.7.0", "indexFile": "resource/10003/3.7.1/370/indexFile.json",
                      "indexFileMd5": "{{Md5(patchIndexFileJson)}}", "baseUrl": "launcher/game/G152/10003/3.7.1/patch370/" }
                  ]
                }
              }
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/10003/3.7.1/370/indexFile.json", patchIndexFileJson);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetIncrementalManifestAsync(Server(), "3.7.0", "3.7.1", "hd"));

        Assert.Contains("escapes", ex.Message);
    }

    [Fact]
    public async Task GetManifest_TargetTierBaseEntryMissingFromPack_RejectsInsteadOfDroppingIt()
    {
        // 目标档条目在 base 里、却不在档位包里：裁掉后它既不在清单、也不在保留名单（PreservedPaths
        // 只保其它档），同步收尾清理会把它当游离文件删除且无自愈路径——按"缺目标档包拒收"同一
        // 哲学显式拒收，而不是静默丢条目（2026-10-23 复审）。
        // 夹具按实测形态给 common + HD（生产 base 是 common + HD，目标档条目是假设的异常形态）。
        RegisterStandardFixture(indexFileJson: """
            {
              "resource": [
                { "dest": "Client/Binaries/Win64/Client-Win64-Shipping.exe", "md5": "bbbb2222bbbb2222bbbb2222bbbb2222", "size": 20 },
                { "dest": "Client/Content/SD/only-in-base.pak", "md5": "aaaa1111aaaa1111aaaa1111aaaa1111", "size": 10 }
              ]
            }
            """);
        RegisterTierFixture("sd", PackIndexFileJson("SD", ("new.pak", 100)));

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "sd"));

        Assert.Contains("does not contain", ex.Message);
    }

    [Fact]
    public async Task GetManifest_TargetTierBaseEntryCoveredByPack_UsesPackEntry()
    {
        // 目标档条目在 base 里且被档位包收录：base 那份被裁掉，以档位包为唯一来源（同路径只留一条、
        // 校验信息取包）。若把"裁掉目标档"退回"只裁其它档"，同路径会出现两条 → 断言失败。
        RegisterStandardFixture(indexFileJson: """
            {
              "resource": [
                { "dest": "Client/Binaries/Win64/Client-Win64-Shipping.exe", "md5": "bbbb2222bbbb2222bbbb2222bbbb2222", "size": 20 },
                { "dest": "Client/Content/SD/shared.pak", "md5": "aaaa1111aaaa1111aaaa1111aaaa1111", "size": 10 }
              ]
            }
            """);
        RegisterTierFixture("sd", PackIndexFileJson("SD", ("shared.pak", 4242)));

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.7.1", "sd");

        Assert.Equal(2, manifest.Files.Count); // 裁剩的 common + 档位包那份
        var entry = Assert.Single(
            manifest.Files, f => f.Path == "Client/Content/SD/shared.pak");
        Assert.Equal(4242, entry.Size); // 取档位包那份，不是 base 的 10
    }

    [Fact]
    public async Task GetManifest_TierPackEntryOutsideTier_Throws()
    {
        // 档位包条目必须全在目标档目录下：混入 HD 条目会在 SD 同步时把 HD 内容写进清单，
        // 后续清理/切档语义全乱
        RegisterStandardFixture();
        RegisterTierFixture("uhd", PackIndexFileJson("HD", ("evil.pak", 10)));

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "uhd"));

        Assert.Contains("outside", ex.Message);
    }

    [Fact]
    public async Task GetIncrementalManifest_DeleteFilesReservedPath_Filtered()
    {
        // 废弃清单点名 .yagl/Saved 时必须跳过（保留路径是用户数据/启动器状态）
        var patchIndexFileJson = """
            {
              "resource": [],
              "deleteFiles": [
                ".yagl/state.json",
                "Saved/slot.sav",
                "Client/Content/Paks/gone.pak"
              ]
            }
            """;
        RegisterStandardFixture();
        var indexJson = $$"""
            {
              "default": {
                "version": "3.7.1",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.7.1/token/zip/",
                "config": {
                  "version": "3.7.1",
                  "indexFile": "resource/10003/3.7.1/token/indexFile.json",
                  "indexFileMd5": "{{Md5("ignored")}}",
                  "patchConfig": [
                    { "version": "3.7.0", "indexFile": "resource/10003/3.7.1/370/indexFile.json",
                      "indexFileMd5": "{{Md5(patchIndexFileJson)}}", "baseUrl": "launcher/game/G152/10003/3.7.1/patch370/" }
                  ]
                }
              }
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/10003/3.7.1/370/indexFile.json", patchIndexFileJson);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.7.0", "3.7.1", "hd");

        Assert.NotNull(manifest);
        Assert.Equal(["Client/Content/Paks/gone.pak"], manifest.DeleteFiles);
    }

    [Fact]
    public async Task GetIncrementalManifest_BlankDeleteFilesEntry_SkippedInsteadOfThrowing()
    {
        // `"deleteFiles": [null]` 会被 STJ 解析出 null 元素（2026-10-26 复核实测）：它是数据毛刺、
        // 不指向任何可删文件，跳过即可——不能让它 NRE 掉整份清单获取（F42 同族）。
        var patchIndexFileJson = """
            {
              "resource": [],
              "deleteFiles": [ null, "", "Client/Content/Paks/gone.pak" ]
            }
            """;
        RegisterStandardFixture();
        var indexJson = $$"""
            {
              "default": {
                "version": "3.7.1",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.7.1/token/zip/",
                "config": {
                  "version": "3.7.1",
                  "indexFile": "resource/10003/3.7.1/token/indexFile.json",
                  "indexFileMd5": "{{Md5("ignored")}}",
                  "patchConfig": [
                    { "version": "3.7.0", "indexFile": "resource/10003/3.7.1/370/indexFile.json",
                      "indexFileMd5": "{{Md5(patchIndexFileJson)}}", "baseUrl": "launcher/game/G152/10003/3.7.1/patch370/" }
                  ]
                }
              }
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/10003/3.7.1/370/indexFile.json", patchIndexFileJson);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.7.0", "3.7.1", "hd");

        Assert.NotNull(manifest);
        Assert.Equal(["Client/Content/Paks/gone.pak"], manifest.DeleteFiles);
    }

    // ---------- 全量清单：hd / 未设置 = 标准清单不变（不取档位索引）----------

    [Fact]
    public async Task GetManifest_HdTier_FallsBackToStandardIndexWhenBundleIndexUnavailable()
    {
        RegisterStandardFixture();
        // 旧档位索引（无 bundles）不是 3.8 bundle 协议；这里故意不注册档位索引：
        // bundle 探测失败后 hd 仍走标准清单，不得因探测失败而报错。
        // （3.8+ 的 hd 由 bundle 的 common + hd 合并，见 KuroBundleIndexTests。）

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.7.1", "hd");

        Assert.Equal(
            ["Client/Binaries/Win64/Client-Win64-Shipping.exe",
             "Client/Content/HD/pakchunk1-HD-WindowsNoEditor.pak",
             "Client/Content/HD/pakchunk1-HD-WindowsNoEditor.sig"],
            [.. manifest.Files.Select(f => f.Path)]);
        Assert.Equal("hd", manifest.ResourceTier);
        Assert.Equal(
            ["Client/Content/SD", "Client/Content/UHD"],
            manifest.PreservedPaths.OrderBy(p => p));
    }

    [Fact]
    public async Task GetManifest_NoTierConfigured_StandardManifestWithTierDirsPreserved()
    {
        RegisterStandardFixture();

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.7.1");

        Assert.Equal(3, manifest.Files.Count);
        Assert.Equal("hd", manifest.ResourceTier);
        Assert.Equal(["Client/Content/SD", "Client/Content/UHD"], manifest.PreservedPaths.OrderBy(p => p));
    }

    [Fact]
    public async Task GetManifest_ServerWithoutTierIndex_IgnoresTierAndKeepsStandardManifest()
    {
        // 国际服/B 服实测无档位索引（404）：档位只作启动参数，下载恒为官方默认 bundle（common + HD）——
        // 不得因服务器不支持档位就抛错或裁掉 HD（否则会把已有内容判成游离文件）
        RegisterStandardFixture("https://prod-global.example.com/launcher/game/G153/10003_token/index.json");

        var manifest = await CreateApi().GetManifestAsync(ServerWithoutTierIndex(), "3.7.1", "uhd");

        Assert.Equal(3, manifest.Files.Count);
        Assert.Equal("hd", manifest.ResourceTier);
        Assert.DoesNotContain("Client/Content/HD", manifest.PreservedPaths);
    }

    [Fact]
    public async Task SupportsResourcePackDownload_TrueOnlyWithOption()
    {
        var api = CreateApi();

        Assert.True(api.SupportsResourcePackDownload(Server()));
        Assert.False(api.SupportsResourcePackDownload(ServerWithoutTierIndex()));

        var blank = Server();
        blank.Options["resourcePackIndexUrl"] = "  ";
        Assert.False(api.SupportsResourcePackDownload(blank));
    }

    // ---------- 防误删守卫 ----------

    [Fact]
    public async Task GetManifest_TierMissingFromResourcePacks_ThrowsActionable()
    {
        RegisterStandardFixture();
        RegisterTierFixture("uhd", PackIndexFileJson("UHD", ("pakchunk1-UHD-WindowsNoEditor.pak", 100)));

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "sd"));

        Assert.Contains("sd", ex.Message);
    }

    [Fact]
    public async Task GetManifest_TierIndexFetchFails_ThrowsInsteadOfReturningPacklessManifest()
    {
        // 档位索引拉不到（网络/服务器问题）：绝不能返回"缺目标档位包"的清单——那会让收尾清理
        // 把目标档位目录当游离文件删掉（几十 GB）；失败须带档位上下文地拒收
        RegisterStandardFixture();
        _downloader.FailUrls.Add(TierIndexUrl);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "uhd"));

        Assert.Contains("resource pack index", ex.Message);
    }

    [Fact]
    public async Task GetManifest_TierPackIndexEmpty_ThrowsInsteadOfReturningPacklessManifest()
    {
        // 包清单 0 条 = 拉到残缺/异常响应的信号：同样不能返回"缺目标档位包"的清单
        RegisterStandardFixture();
        RegisterTierFixture("uhd", "{\n  \"resource\": []\n}");

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.7.1", "uhd"));

        Assert.Contains("lists no files", ex.Message);
    }

    [Fact]
    public async Task GetManifest_TierIndex_FallsBackToStandardCdnWhenTierCdnUnusable()
    {
        RegisterStandardFixture();
        var packIndexFileJson = PackIndexFileJson("UHD", ("pakchunk1-UHD-WindowsNoEditor.pak", 100));
        var tierIndexJson = $$"""
            {
              "cdnList": [ { "P": 1, "K1": 0, "K2": 1, "url": "https://cdn-dead.example.com/" } ],
              "resourcePacks": {
                "uhd": {
                  "version": "3.7.1",
                  "indexFile": "tier/uhd/indexFile.json",
                  "indexFileMd5": "{{Md5(packIndexFileJson)}}",
                  "baseUrl": "tier/zip/"
                }
              }
            }
            """;
        _downloader.Serve(TierIndexUrl, tierIndexJson);
        // 档位索引 CDN 全节点不可用 → 回退标准块 CDN 取包清单
        _downloader.Serve(Cdn + "tier/uhd/indexFile.json", packIndexFileJson);

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.7.1", "uhd");

        Assert.Contains(manifest.Files, f => f.Path == "Client/Content/UHD/pakchunk1-UHD-WindowsNoEditor.pak");
    }

    // ---------- 增量清单：只裁剪其它档位目录（档位包落盘由更新后的全量修复轮补）----------

    [Fact]
    public async Task GetIncrementalManifest_SdTier_CropsOtherTierFilesAndGroups()
    {
        var patchIndexFileJson = """
            {
              "resource": [
                { "dest": "Client/Content/Paks/brand-new.pak", "md5": "12341234123412341234123412341234", "size": 40 },
                { "dest": "Client/Content/HD/pakchunk1-HD-WindowsNoEditor.pak", "md5": "56785678567856785678567856785678", "size": 50 },
                { "dest": "Client/Content/Paks/changed.pak.krpdiff", "md5": "9abc9abc9abc9abc9abc9abc9abc9abc", "size": 60 }
              ],
              "groupInfos": [
                {
                  "dest": "Client/Content/Paks/changed.pak",
                  "srcFiles": [ { "dest": "Client/Content/Paks/changed.pak", "md5": "11111111111111111111111111111111", "size": 70 } ],
                  "dstFiles": [ { "dest": "Client/Content/Paks/changed.pak", "md5": "22222222222222222222222222222222", "size": 80 } ]
                },
                {
                  "dest": "Client/Content/HD/pakchunk1-HD-WindowsNoEditor.pak",
                  "srcFiles": [ { "dest": "Client/Content/HD/pakchunk1-HD-WindowsNoEditor.pak", "md5": "33333333333333333333333333333333", "size": 90 } ],
                  "dstFiles": [ { "dest": "Client/Content/HD/pakchunk1-HD-WindowsNoEditor.pak", "md5": "44444444444444444444444444444444", "size": 95 } ]
                }
              ],
              "deleteFiles": [ "Client/Content/HD/pakchunk0-HD-WindowsNoEditor.pak", "Client/Content/Paks/gone.pak" ]
            }
            """;
        RegisterStandardFixture();
        // 增量清单不含档位包内容，版本守卫不在这里（见 GetManifestAsync / 预下载窗口用例），
        // 故不注册档位索引：它本就不该被本路径请求。

        var indexJson = $$"""
            {
              "default": {
                "version": "3.7.1",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.7.1/token/zip/",
                "config": {
                  "version": "3.7.1",
                  "indexFile": "resource/10003/3.7.1/token/indexFile.json",
                  "indexFileMd5": "{{Md5("ignored")}}",
                  "patchConfig": [
                    { "version": "3.7.0", "indexFile": "resource/10003/3.7.1/370/indexFile.json",
                      "indexFileMd5": "{{Md5(patchIndexFileJson)}}", "baseUrl": "launcher/game/G152/10003/3.7.1/patch370/" }
                  ]
                }
              }
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/10003/3.7.1/370/indexFile.json", patchIndexFileJson);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.7.0", "3.7.1", "sd");

        Assert.NotNull(manifest);
        Assert.Equal(["Client/Content/Paks/brand-new.pak"], [.. manifest.Files.Select(f => f.Path)]);
        Assert.Equal(["Client/Content/Paks/changed.pak"], [.. manifest.Groups.Select(g => g.PatchFile)]);
        // 阻塞项（2026-10-19 review）：官方废弃清单同样可能点名其它档位目录——原样透传会让 SD/UHD
        // 更新删掉 HD 目录里清单点名的文件，与"切档不删旧档、删除只经显式清理"冲突
        Assert.Equal(["Client/Content/Paks/gone.pak"], manifest.DeleteFiles);
        Assert.Equal("sd", manifest.ResourceTier);
        Assert.Equal(
            ["Client/Content/HD", "Client/Content/UHD"],
            manifest.PreservedPaths.OrderBy(p => p));
    }

    [Fact]
    public async Task GetIncrementalManifest_PredownloadWindow_TierPackNotYetAtTarget_DoesNotHardFail()
    {
        // 预下载窗口期：toVersion（预下载目标版 3.8.0）按定义不是线上版（3.7.1），而
        // resourcePacks[tier].version 是单值（3.7.1）。差量清单本身不含档位包内容，档位包落盘由
        // 应用后的全量修复轮承担（那里 GetManifestAsync → EnsurePackVersion 才校验版本）——
        // 在此硬失败会把 sd/uhd 的预下载整个挡死且无解（2026-10-23 复审阻塞项）。
        var patchIndexFileJson = """
            {
              "resource": [
                { "dest": "Client/Content/Paks/brand-new.pak", "md5": "12341234123412341234123412341234", "size": 40 }
              ]
            }
            """;
        RegisterStandardFixture(version: "3.7.1");
        RegisterTierFixture("sd", PackIndexFileJson("SD", ("pakchunk1-SD-WindowsNoEditor.pak", 100)),
            version: "3.7.1", packVersion: "3.7.1");
        var indexJson = $$"""
            {
              "default": {
                "version": "3.7.1",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.7.1/token/zip/",
                "config": {
                  "version": "3.7.1",
                  "indexFile": "resource/10003/3.7.1/token/indexFile.json",
                  "indexFileMd5": "{{Md5("ignored")}}"
                }
              },
              "predownload": {
                "version": "3.8.0",
                "resourcesBasePath": "launcher/game/G152/10003/3.8.0/token/zip/",
                "config": {
                  "version": "3.8.0",
                  "patchConfig": [
                    { "version": "3.7.0", "indexFile": "resource/10003/3.8.0/370/indexFile.json",
                      "indexFileMd5": "{{Md5(patchIndexFileJson)}}", "baseUrl": "launcher/game/G152/10003/3.8.0/patch370/" }
                  ]
                }
              }
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/10003/3.8.0/370/indexFile.json", patchIndexFileJson);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.7.0", "3.8.0", "sd");

        Assert.NotNull(manifest);
        Assert.Equal("3.8.0", manifest.Version);
        Assert.Equal("sd", manifest.ResourceTier);
    }

    [Fact]
    public async Task GetIncrementalManifest_HdTier_KeepsHdEntries()
    {
        var patchIndexFileJson = """
            {
              "resource": [
                { "dest": "Client/Content/HD/pakchunk1-HD-WindowsNoEditor.pak", "md5": "56785678567856785678567856785678", "size": 50 }
              ]
            }
            """;
        RegisterStandardFixture();
        var indexJson = $$"""
            {
              "default": {
                "version": "3.7.1",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.7.1/token/zip/",
                "config": {
                  "version": "3.7.1",
                  "indexFile": "resource/10003/3.7.1/token/indexFile.json",
                  "indexFileMd5": "{{Md5("ignored")}}",
                  "patchConfig": [
                    { "version": "3.7.0", "indexFile": "resource/10003/3.7.1/370/indexFile.json",
                      "indexFileMd5": "{{Md5(patchIndexFileJson)}}", "baseUrl": "launcher/game/G152/10003/3.7.1/patch370/" }
                  ]
                }
              }
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/10003/3.7.1/370/indexFile.json", patchIndexFileJson);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.7.0", "3.7.1", "hd");

        Assert.NotNull(manifest);
        Assert.Equal("hd", manifest.ResourceTier);
        Assert.Single(manifest.Files);
    }

    // ---------- 档位包清单（测体积/清理用）----------

    [Fact]
    public async Task GetResourcePackManifest_ReturnsPackFilesWithDirectory()
    {
        RegisterTierFixture("uhd", PackIndexFileJson("UHD", ("pakchunk7-UHD-WindowsNoEditor.pak", 700), ("pakchunk7-UHD-WindowsNoEditor.sig", 9)));

        var pack = await CreateApi().GetResourcePackManifestAsync(Server(), "uhd");

        Assert.NotNull(pack);
        Assert.Equal("uhd", pack.Tier);
        Assert.Equal("Client/Content/UHD", pack.PackDirectory);
        Assert.Equal("3.7.1", pack.Version);
        Assert.Equal(
            ["Client/Content/UHD/pakchunk7-UHD-WindowsNoEditor.pak", "Client/Content/UHD/pakchunk7-UHD-WindowsNoEditor.sig"],
            [.. pack.Files.Select(f => f.Path)]);
        Assert.Equal($"{TierCdn}launcher/game/G152/10003/3.7.1/token/zip/Client/Content/UHD/pakchunk7-UHD-WindowsNoEditor.pak", pack.Files[0].Url);
    }

    [Fact]
    public async Task GetResourcePackManifest_EntryOutsideTier_Throws()
    {
        // 清理/测量路径同样不能信包清单：混入别的档位会被当成本档体积，甚至清理时误删
        RegisterTierFixture("uhd", PackIndexFileJson("HD", ("evil.pak", 10)));

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetResourcePackManifestAsync(Server(), "uhd"));

        Assert.Contains("outside", ex.Message);
    }

    [Theory]
    [InlineData("hdr")]
    [InlineData("")]
    [InlineData("HD")]
    public async Task GetResourcePackManifest_UnknownTier_ReturnsNull(string tier)
    {
        Assert.Null(await CreateApi().GetResourcePackManifestAsync(Server(), tier));
    }

    [Fact]
    public async Task GetResourcePackManifest_ServerWithoutTierIndex_ReturnsNullWithoutNetwork()
    {
        // 不注册任何响应：不支持档位的服务器必须零网络返回 null（清理入口对它禁用）
        Assert.Null(await CreateApi().GetResourcePackManifestAsync(ServerWithoutTierIndex(), "uhd"));
    }

    [Fact]
    public async Task GetResourcePackManifest_TierMissingFromResourcePacks_ReturnsNull()
    {
        RegisterTierFixture("uhd", PackIndexFileJson("UHD", ("pakchunk7-UHD-WindowsNoEditor.pak", 700)));

        Assert.Null(await CreateApi().GetResourcePackManifestAsync(Server(), "sd"));
    }
}
