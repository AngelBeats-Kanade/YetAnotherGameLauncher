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

    /// <summary>标准 index.json + 标准全量清单（common + 100 条式样的 HD 条目）。</summary>
    private (string IndexJson, string IndexFileJson) RegisterStandardFixture(
        string indexUrl = "https://prod-cn.example.com/launcher/game/G152/10003_token/index.json")
    {
        var indexFileJson = """
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
                "version": "3.7.1",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.7.1/token/zip/",
                "config": {
                  "version": "3.7.1",
                  "indexFile": "resource/10003/3.7.1/token/indexFile.json",
                  "indexFileMd5": "{{Md5(indexFileJson)}}",
                  "baseUrl": "launcher/game/G152/10003/3.7.1/token/zip/"
                }
              }
            }
            """;

        _downloader.Serve(indexUrl, indexJson);
        _downloader.Serve(Cdn + "resource/10003/3.7.1/token/indexFile.json", indexFileJson);
        return (indexJson, indexFileJson);
    }

    /// <summary>档位索引 + 指定档位的包清单，注册到假下载器；返回包清单 JSON。</summary>
    private string RegisterTierFixture(string tier, string packIndexFileJson)
    {
        var tierIndexJson = $$"""
            {
              "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{TierCdn}}" } ],
              "config": { "version": "3.7.1" },
              "resourcePacks": {
                "{{tier}}": {
                  "version": "3.7.1",
                  "indexFile": "launcher/game/G152/10003/3.7.1/token/{{tier}}/indexFile.json",
                  "indexFileMd5": "{{Md5(packIndexFileJson)}}",
                  "baseUrl": "launcher/game/G152/10003/3.7.1/token/zip/",
                  "size": 123
                }
              }
            }
            """;
        _downloader.Serve(TierIndexUrl, tierIndexJson);
        _downloader.Serve(
            TierCdn + $"launcher/game/G152/10003/3.7.1/token/{tier}/indexFile.json", packIndexFileJson);
        return tierIndexJson;
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

    // ---------- 全量清单：hd / 未设置 = 标准清单不变（不取档位索引）----------

    [Fact]
    public async Task GetManifest_HdTier_UsesStandardIndexWithoutFetchingTierIndex()
    {
        RegisterStandardFixture();
        // 档位索引故意不注册：hd 档走标准清单（官方 hd 包与标准 HD 条目逐字节一致），不得联网取它

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
        Assert.Equal(["Client/Content/Paks/brand-new.pak"], [.. manifest!.Files.Select(f => f.Path)]);
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
        Assert.Equal("hd", manifest!.ResourceTier);
        Assert.Single(manifest.Files);
    }

    // ---------- 档位包清单（测体积/清理用）----------

    [Fact]
    public async Task GetResourcePackManifest_ReturnsPackFilesWithDirectory()
    {
        RegisterTierFixture("uhd", PackIndexFileJson("UHD", ("pakchunk7-UHD-WindowsNoEditor.pak", 700), ("pakchunk7-UHD-WindowsNoEditor.sig", 9)));

        var pack = await CreateApi().GetResourcePackManifestAsync(Server(), "uhd");

        Assert.NotNull(pack);
        Assert.Equal("uhd", pack!.Tier);
        Assert.Equal("Client/Content/UHD", pack.PackDirectory);
        Assert.Equal("3.7.1", pack.Version);
        Assert.Equal(
            ["Client/Content/UHD/pakchunk7-UHD-WindowsNoEditor.pak", "Client/Content/UHD/pakchunk7-UHD-WindowsNoEditor.sig"],
            [.. pack.Files.Select(f => f.Path)]);
        Assert.Equal($"{TierCdn}launcher/game/G152/10003/3.7.1/token/zip/Client/Content/UHD/pakchunk7-UHD-WindowsNoEditor.pak", pack.Files[0].Url);
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
