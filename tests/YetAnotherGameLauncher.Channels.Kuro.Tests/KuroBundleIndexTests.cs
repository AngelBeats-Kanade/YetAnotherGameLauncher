using System.Text;
using System.Text.Json;
using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

/// <summary>
/// 3.8+ 官方 bundle 索引（resourcePacks + bundles + 同构 predownload）：
/// 版本来自各包 version，全量/增量清单由 common + 当前档位包合并，档位包允许写
/// Client/Saved/Resources/&lt;目标版本&gt;/ 的版本化可选包。
/// </summary>
public class KuroBundleIndexTests
{
    private const string Cdn = "https://cdn-bundle.example.com/";
    private const string IndexUrl = "https://prod-cn.example.com/launcher/game/G152/10003_token/index.json";
    private const string BundleIndexUrl = "https://prod-cn.example.com/launcher/game/G152/10003_token/official/index.json";
    private const string Tier = "uhd";

    private readonly FakeDownloader _downloader = new();

    private KuroChannelApi CreateApi() => new(_downloader);

    private static GameServer Server() => new()
    {
        Id = "cn",
        Name = "国服",
        Options = new Dictionary<string, string>
        {
            ["indexUrl"] = IndexUrl,
            ["resourcePackIndexUrl"] = BundleIndexUrl,
        },
    };

    private static string Md5(string text) => Hashing.Md5Hex(Encoding.UTF8.GetBytes(text));

    private static string FullCommon(string version) => $$"""
        {
          "resource": [
            { "dest": "Client/Binaries/Win64/Client-Win64-Shipping.exe", "md5": "{{Md5("exe")}}", "size": 10 },
            { "dest": "Client/Content/Paks/common.pak", "md5": "{{Md5("common-pak")}}", "size": 20 },
            { "dest": "Client/Saved/Resources/{{version}}/Option_Pack_Common/Base/common-option.pak",
              "md5": "{{Md5("common-option")}}", "size": 5 }
          ]
        }
        """;

    private static string FullTier(string tier, string version) => $$"""
        {
          "resource": [
            { "dest": "Client/Content/{{tier.ToUpperInvariant()}}/pakchunk1-{{tier.ToUpperInvariant()}}.pak",
              "md5": "{{Md5(tier + "-pak")}}", "size": 30 },
            { "dest": "Client/Saved/Resources/{{version}}/Option_Pack_{{tier.ToUpperInvariant()}}/Base/option.pak",
              "md5": "{{Md5(tier + "-option")}}", "size": 7 }
          ]
        }
        """;

    private static string PatchCommon(string prefix, string toVersion, string fromVersion) => $$"""
        {
          "resource": [
            { "dest": "Client/Content/Paks/new.pak", "md5": "{{Md5("new-common")}}", "size": 40,
              "fromFolder": "{{prefix}}/{{toVersion}}/zip/" },
            { "dest": "{{fromVersion}}_{{toVersion}}_common.krpdiff", "md5": "{{Md5("common-diff")}}", "size": 100 }
          ],
          "groupInfos": [
            {
              "dest": "{{fromVersion}}_{{toVersion}}_common.krpdiff",
              "size": 100,
              "md5": "{{Md5("common-diff")}}",
              "srcFiles": [ { "dest": "Client/Content/Paks/changed.pak", "md5": "{{Md5("old-common")}}", "size": 50 } ],
              "dstFiles": [ { "dest": "Client/Content/Paks/changed.pak", "md5": "{{Md5("new-common-changed")}}", "size": 55 } ]
            }
          ],
          "deleteFiles": [
            "Client/Content/Paks/gone.pak",
            "Client/Content/UHD/other-tier-gone.pak",
            "Client/Saved/Resources/{{toVersion}}/Option/old.pak"
          ]
        }
        """;

    private static string PatchTier(string tier, string prefix, string toVersion, string fromVersion)
    {
        var dir = tier.ToUpperInvariant();
        var other = tier.Equals("sd", StringComparison.OrdinalIgnoreCase) ? "uhd" : "sd";
        return $$"""
            {
              "resource": [
                { "dest": "Client/Content/{{dir}}/new.pak", "md5": "{{Md5(tier + "-new")}}", "size": 40,
                  "fromFolder": "{{prefix}}/{{toVersion}}/zip/" },
                { "dest": "{{fromVersion}}_{{toVersion}}_{{tier}}.krpdiff", "md5": "{{Md5(tier + "-diff")}}", "size": 80 }
              ],
              "groupInfos": [
                {
                  "dest": "{{fromVersion}}_{{toVersion}}_{{tier}}.krpdiff",
                  "size": 80,
                  "md5": "{{Md5(tier + "-diff")}}",
                  "srcFiles": [ { "dest": "Client/Content/{{dir}}/changed.pak", "md5": "{{Md5(tier + "-old")}}", "size": 60 } ],
                  "dstFiles": [ { "dest": "Client/Content/{{dir}}/changed.pak", "md5": "{{Md5(tier + "-new-changed")}}", "size": 65 } ]
                }
              ],
              "deleteFiles": [
                "Client/Content/{{dir}}/gone.pak",
                "Client/Content/{{other.ToUpperInvariant()}}/other-tier-gone.pak"
              ]
            }
            """;
    }

    private (Dictionary<string, object> Packs, Dictionary<string, object> Bundles) ServePackSet(
        string prefix,
        string version,
        string fromVersion,
        bool includeCommonPatch = true,
        bool includeTierPatch = true,
        string? commonFullOverride = null,
        string? tierFullOverride = null,
        string? commonVersionOverride = null,
        string? tierVersionOverride = null)
    {
        var packs = new Dictionary<string, object>(StringComparer.Ordinal);
        var bundles = new Dictionary<string, object>(StringComparer.Ordinal);

        var commonFullJson = commonFullOverride ?? FullCommon(version);
        var commonFullPath = $"{prefix}/{version}/common/indexFile.json";
        _downloader.Serve(Cdn + commonFullPath, commonFullJson);

        var commonPatchJson = PatchCommon(prefix, version, fromVersion);
        var commonPatchPath = $"{prefix}/{version}/common/{fromVersion}/indexFile.json";
        if (includeCommonPatch)
        {
            _downloader.Serve(Cdn + commonPatchPath, commonPatchJson);
        }

        packs["common"] = new Dictionary<string, object?>
        {
            ["version"] = commonVersionOverride ?? version,
            ["indexFile"] = commonFullPath,
            ["indexFileMd5"] = Md5(commonFullJson),
            ["baseUrl"] = $"{prefix}/{version}/zip/",
            ["patchConfig"] = includeCommonPatch
                ? new[]
                {
                    new
                    {
                        version = fromVersion,
                        indexFile = commonPatchPath,
                        indexFileMd5 = Md5(commonPatchJson),
                        baseUrl = $"{prefix}/{version}/common/{fromVersion}/resources",
                    },
                }
                : Array.Empty<object>(),
        };

        foreach (var tier in new[] { "hd", "sd", "uhd" })
        {
            var tierJson = tier == Tier && tierFullOverride is not null
                ? tierFullOverride
                : FullTier(tier, version);
            var tierFullPath = $"{prefix}/{version}/{tier}/indexFile.json";
            _downloader.Serve(Cdn + tierFullPath, tierJson);

            var tierPatchJson = PatchTier(tier, prefix, version, fromVersion);
            var tierPatchPath = $"{prefix}/{version}/{tier}/{fromVersion}/indexFile.json";
            if (includeTierPatch)
            {
                _downloader.Serve(Cdn + tierPatchPath, tierPatchJson);
            }

            packs[tier] = new Dictionary<string, object?>
            {
                ["version"] = tier == Tier && tierVersionOverride is not null
                    ? tierVersionOverride
                    : version,
                ["indexFile"] = tierFullPath,
                ["indexFileMd5"] = Md5(tierJson),
                ["baseUrl"] = $"{prefix}/{version}/zip/",
                ["patchConfig"] = includeTierPatch
                    ? new[]
                    {
                        new
                        {
                            version = fromVersion,
                            indexFile = tierPatchPath,
                            indexFileMd5 = Md5(tierPatchJson),
                            baseUrl = $"{prefix}/{version}/{tier}/{fromVersion}/resources",
                        },
                    }
                    : Array.Empty<object>(),
            };
            bundles[tier.ToUpperInvariant()] = new { resourcePacks = new[] { "common", tier } };
        }

        return (packs, bundles);
    }

    private void ServeBundle(
        string formalVersion = "3.8.0",
        string? predownloadVersion = null,
        bool predownloadSwitch = true,
        bool includeCommonPatch = true,
        bool includeTierPatch = true,
        string? commonFullOverride = null,
        string? tierFullOverride = null,
        string? commonVersionOverride = null,
        string? tierVersionOverride = null)
    {
        var (packs, bundles) = ServePackSet(
            "bundle",
            formalVersion,
            "3.7.1",
            includeCommonPatch,
            includeTierPatch,
            commonFullOverride,
            tierFullOverride,
            commonVersionOverride,
            tierVersionOverride);

        var root = new Dictionary<string, object?>
        {
            ["cdnList"] = new[] { new { P = 1, K1 = 1, K2 = 1, url = Cdn } },
            ["config"] = new { predownloadSwitch = predownloadSwitch ? 1 : 0 },
            ["bundles"] = bundles,
            ["resourcePacks"] = packs,
        };

        if (predownloadVersion is not null)
        {
            var (prePacks, preBundles) = ServePackSet(
                "pre",
                predownloadVersion,
                formalVersion);
            root["predownload"] = new Dictionary<string, object?>
            {
                ["bundles"] = preBundles,
                ["resourcePacks"] = prePacks,
            };
        }

        _downloader.Serve(BundleIndexUrl, JsonSerializer.Serialize(root));
    }

    private void ServeLegacy(string version = "3.7.1")
    {
        var fullJson = """
            {
              "resource": [
                { "dest": "Client/Content/HD/pakchunk1-HD-WindowsNoEditor.pak",
                  "md5": "bbbb2222bbbb2222bbbb2222bbbb2222", "size": 20 }
              ]
            }
            """;
        var indexJson = $$"""
            {
              "default": {
                "version": "{{version}}",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "legacy/{{version}}/zip/",
                "config": {
                  "version": "{{version}}",
                  "indexFile": "legacy/{{version}}/indexFile.json",
                  "indexFileMd5": "{{Md5(fullJson)}}",
                  "baseUrl": "legacy/{{version}}/zip/"
                }
              }
            }
            """;
        _downloader.Serve(IndexUrl, indexJson);
        _downloader.Serve(Cdn + $"legacy/{version}/indexFile.json", fullJson);
    }

    private static string PathOf(ManifestFile file) => file.Path.Replace('\\', '/');

    // ---------- 版本检测 ----------

    [Fact]
    public async Task GetVersionInfo_BundleIndex_UsesPackVersionAndPredownload()
    {
        ServeBundle(predownloadVersion: "3.9.0");

        var info = await CreateApi().GetVersionInfoAsync(Server());

        Assert.Equal("3.8.0", info.LatestVersion);
        Assert.Contains("3.7.1", info.PatchSourceVersions);
        Assert.True(info.PredownloadAvailable);
        Assert.Equal("3.9.0", info.PredownloadVersion);
        Assert.Contains("3.8.0", info.PredownloadPatchSourceVersions);
        Assert.DoesNotContain(IndexUrl, _downloader.Requests);
    }

    [Fact]
    public async Task GetVersionInfo_BundleIndex_PredownloadSwitchOff_HidesWindow()
    {
        ServeBundle(predownloadVersion: "3.9.0", predownloadSwitch: false);

        var info = await CreateApi().GetVersionInfoAsync(Server());

        Assert.False(info.PredownloadAvailable);
        Assert.Equal("3.9.0", info.PredownloadVersion);
    }

    [Fact]
    public async Task GetVersionInfo_LegacyTierIndex_FallsBackToLegacyVersion()
    {
        // 旧档位索引（只有 resourcePacks，没有 bundles）不是 bundle 协议：版本仍取旧 index.json。
        var tierOnly = $$"""
            {
              "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
              "resourcePacks": {
                "uhd": { "version": "3.7.1", "indexFile": "legacy/uhd/indexFile.json", "baseUrl": "legacy/zip/" }
              }
            }
            """;
        _downloader.Serve(BundleIndexUrl, tierOnly);
        ServeLegacy();

        var info = await CreateApi().GetVersionInfoAsync(Server());

        Assert.Equal("3.7.1", info.LatestVersion);
        Assert.Contains(IndexUrl, _downloader.Requests);
    }

    [Fact]
    public async Task GetVersionInfo_BundleIndexFetchFails_ThrowsInsteadOfReportingStaleLegacy()
    {
        // 新索引不可达时不能退回旧的 3.7.1 版本源并谎报"已是最新"；让 UI 明确显示连接失败。
        ServeLegacy();
        _downloader.FailUrls.Add(BundleIndexUrl);

        await Assert.ThrowsAsync<UpdateException>(() => CreateApi().GetVersionInfoAsync(Server()));
    }

    // ---------- 全量清单合并 ----------

    [Fact]
    public async Task GetManifest_BundleIndex_Uhd_MergesCommonAndTierPacks()
    {
        ServeBundle();

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.8.0", "uhd");

        Assert.Equal("3.8.0", manifest.Version);
        Assert.Equal("uhd", manifest.ResourceTier);
        Assert.Equal(
            ["Client/Binaries/Win64/Client-Win64-Shipping.exe",
             "Client/Content/Paks/common.pak",
             "Client/Content/UHD/pakchunk1-UHD.pak",
             "Client/Saved/Resources/3.8.0/Option_Pack_Common/Base/common-option.pak",
             "Client/Saved/Resources/3.8.0/Option_Pack_UHD/Base/option.pak"],
            manifest.Files.Select(PathOf).OrderBy(p => p, StringComparer.Ordinal));
        Assert.All(manifest.Files, f => Assert.StartsWith(Cdn + "bundle/3.8.0/zip/", f.Url));
        Assert.Equal(
            ["Client/Content/HD", "Client/Content/SD"],
            manifest.PreservedPaths.OrderBy(p => p));
        Assert.DoesNotContain(IndexUrl, _downloader.Requests);
    }

    [Fact]
    public async Task GetManifest_BundleIndex_Hd_UsesCommonAndHdPack()
    {
        ServeBundle();

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.8.0", "hd");

        Assert.Contains(manifest.Files, f => PathOf(f) == "Client/Content/HD/pakchunk1-HD.pak");
        Assert.DoesNotContain(manifest.Files, f => PathOf(f).StartsWith("Client/Content/UHD/", StringComparison.Ordinal));
        Assert.Equal("hd", manifest.ResourceTier);
    }

    [Fact]
    public async Task GetManifest_BundleIndex_FullSavedResourcesOtherVersion_Throws()
    {
        var stale = FullTier(Tier, "3.7.1");
        ServeBundle(tierFullOverride: stale);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.8.0", "uhd"));

        Assert.Contains("reserved", ex.Message);
    }

    [Fact]
    public async Task GetManifest_BundleIndex_VersionMismatch_Throws()
    {
        ServeBundle(tierVersionOverride: "3.7.1");

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.8.0", "uhd"));

        Assert.Contains("3.7.1", ex.Message);
    }

    [Fact]
    public async Task GetManifest_BundleIndex_MissingTierBundle_Throws()
    {
        ServeBundle();
        // 重写 bundle 索引，删掉 UHD bundle（resourcePacks 仍完整）。
        var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            Encoding.UTF8.GetString(_downloader.Responses[BundleIndexUrl]))!;
        var bundles = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(root["bundles"].GetRawText())!;
        bundles.Remove("UHD");
        var rewritten = new Dictionary<string, object?>
        {
            ["cdnList"] = root["cdnList"],
            ["config"] = root["config"],
            ["resourcePacks"] = root["resourcePacks"],
            ["bundles"] = bundles,
        };
        _downloader.Serve(BundleIndexUrl, JsonSerializer.Serialize(rewritten));

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.8.0", "uhd"));

        Assert.Contains("UHD", ex.Message);
    }

    [Fact]
    public async Task GetManifest_LegacyVersion_FallsBackToStandardIndex()
    {
        // 请求旧版本（3.7.1）时 bundle 索引不匹配：必须回退标准清单，不能返回 3.8 内容冒充 3.7.1。
        ServeBundle();
        ServeLegacy();

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.7.1", "hd");

        Assert.Equal("3.7.1", manifest.Version);
        Assert.Contains("Client/Content/HD/pakchunk1-HD-WindowsNoEditor.pak", manifest.Files.Select(PathOf));
        Assert.Contains(IndexUrl, _downloader.Requests);
    }

    [Fact]
    public async Task GetManifest_NewerVersionWithoutBundle_FallsBackToLegacyAndRejectsMislabelling()
    {
        // bundle 索引不可达时，旧索引只有 3.7.1；请求 3.8.0 必须拒收，而不是拿 3.7.1 内容标成 3.8.0。
        ServeLegacy();
        _downloader.FailUrls.Add(BundleIndexUrl);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetManifestAsync(Server(), "3.8.0", "hd"));

        Assert.Contains("3.7.1", ex.Message);
    }

    // ---------- 增量清单合并 ----------

    [Fact]
    public async Task GetIncrementalManifest_BundleIndex_MergesCommonAndTierPatches()
    {
        ServeBundle();

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.7.1", "3.8.0", "uhd");

        Assert.NotNull(manifest);
        Assert.Equal("3.8.0", manifest.Version);
        Assert.Equal("uhd", manifest.ResourceTier);

        Assert.Equal(
            ["Client/Content/Paks/new.pak", "Client/Content/UHD/new.pak"],
            manifest.Files.Select(PathOf).OrderBy(p => p, StringComparer.Ordinal));
        Assert.All(manifest.Files, f => Assert.StartsWith(Cdn + "bundle/3.8.0/zip/", f.Url));

        Assert.Equal(
            ["3.7.1_3.8.0_common.krpdiff", "3.7.1_3.8.0_uhd.krpdiff"],
            manifest.Groups.Select(g => g.PatchFile).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Contains(
            manifest.Groups,
            g => g.Url == Cdn + "bundle/3.8.0/common/3.7.1/resources/3.7.1_3.8.0_common.krpdiff");
        Assert.Contains(
            manifest.Groups,
            g => g.Url == Cdn + "bundle/3.8.0/uhd/3.7.1/resources/3.7.1_3.8.0_uhd.krpdiff");

        Assert.Equal(
            ["Client/Content/Paks/gone.pak", "Client/Content/UHD/gone.pak"],
            manifest.DeleteFiles.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(
            ["Client/Content/HD", "Client/Content/SD"],
            manifest.PreservedPaths.OrderBy(p => p));
    }

    [Fact]
    public async Task GetIncrementalManifest_BundleIndex_MissingTierPatch_UsesCommonOnly()
    {
        ServeBundle(includeTierPatch: false);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.7.1", "3.8.0", "uhd");

        Assert.NotNull(manifest);
        // 档位包没有 3.7.1 → 3.8.0 差量时只应用 common 差量；档位内容由更新后的全量修复轮补齐。
        Assert.Equal(["Client/Content/Paks/new.pak"], manifest.Files.Select(PathOf));
        Assert.Equal(["3.7.1_3.8.0_common.krpdiff"], manifest.Groups.Select(g => g.PatchFile));
        Assert.DoesNotContain(
            manifest.DeleteFiles,
            p => p.StartsWith("Client/Content/UHD/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetIncrementalManifest_BundleIndex_MissingCommonPatch_ReturnsNull()
    {
        ServeBundle(includeCommonPatch: false);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.7.1", "3.8.0", "uhd");

        Assert.Null(manifest);
    }

    [Fact]
    public async Task GetIncrementalManifest_BundleIndex_PredownloadTarget_UsesPredownloadPacks()
    {
        ServeBundle(predownloadVersion: "3.9.0");

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.8.0", "3.9.0", "uhd");

        Assert.NotNull(manifest);
        Assert.Equal("3.9.0", manifest.Version);
        Assert.All(manifest.Files, f => Assert.StartsWith(Cdn + "pre/3.9.0/zip/", f.Url));
        Assert.All(manifest.Groups, g => Assert.StartsWith(Cdn + "pre/3.9.0/", g.Url));
    }

    [Fact]
    public async Task GetIncrementalManifest_LegacyVersion_FallsBackToStandardIndex()
    {
        ServeBundle();
        ServeLegacy();

        var patchJson = """
            {
              "resource": [
                { "dest": "Client/Content/Paks/legacy.pak", "md5": "12341234123412341234123412341234", "size": 40 }
              ]
            }
            """;
        var legacy = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            Encoding.UTF8.GetString(_downloader.Responses[IndexUrl]))!;
        var defaultBlock = legacy["default"];
        var config = defaultBlock.GetProperty("config");
        var indexJson = $$"""
            {
              "default": {
                "version": "3.7.1",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "config": {
                  "version": "3.7.1",
                  "indexFile": "{{config.GetProperty("indexFile").GetString()}}",
                  "indexFileMd5": "{{config.GetProperty("indexFileMd5").GetString()}}",
                  "baseUrl": "{{config.GetProperty("baseUrl").GetString()}}",
                  "patchConfig": [
                    { "version": "3.7.0", "indexFile": "legacy/370/indexFile.json",
                      "indexFileMd5": "{{Md5(patchJson)}}", "baseUrl": "legacy/370/" }
                  ]
                }
              }
            }
            """;
        _downloader.Serve(IndexUrl, indexJson);
        _downloader.Serve(Cdn + "legacy/370/indexFile.json", patchJson);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.7.0", "3.7.1", "hd");

        Assert.NotNull(manifest);
        Assert.Equal(["Client/Content/Paks/legacy.pak"], manifest.Files.Select(PathOf));
        Assert.Contains(IndexUrl, _downloader.Requests);
    }

    // ---------- 档位包清单：版本化 Saved/Resources 不得破坏单目录不变量 ----------

    [Fact]
    public async Task GetResourcePackManifest_BundleIndex_AllowsSavedResourcesButReturnsContentOnly()
    {
        ServeBundle();

        var pack = await CreateApi().GetResourcePackManifestAsync(Server(), "uhd");

        Assert.NotNull(pack);
        Assert.Equal("3.8.0", pack.Version);
        Assert.Equal("Client/Content/UHD", pack.PackDirectory);
        Assert.Equal(["Client/Content/UHD/pakchunk1-UHD.pak"], pack.Files.Select(PathOf));
    }
}
