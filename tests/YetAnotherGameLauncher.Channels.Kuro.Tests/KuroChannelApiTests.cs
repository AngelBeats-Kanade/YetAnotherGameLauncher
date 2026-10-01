using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

public class KuroChannelApiTests
{
    private const string Cdn = "https://cdn-a.example.com/";

    private readonly FakeDownloader _downloader = new();

    private KuroChannelApi CreateApi() => new(_downloader);

    private static GameServer Server() => new()
    {
        Id = "cn",
        Name = "国服",
        Options = new Dictionary<string, string>
        {
            ["indexUrl"] = "https://prod-cn.example.com/launcher/game/G152/10003_token/index.json",
        },
    };

    /// <summary>构造 index.json 与全量清单 fixture 并注册到假下载器。predownloadSwitch 传 JSON 字面量（"1"/"0"/"null"）；
    /// predownload 块不带 cdnList（对齐 2026-09-29 真机形态，CDN 回退由 FallsBackToDefaultCdn 用例守护）。</summary>
    private (string IndexJson, string IndexFileJson) RegisterFullFixture(
        bool includePredownload = true,
        string predownloadSwitch = "1")
    {
        var indexFileJson = """
            {
              "resource": [
                { "dest": "Client/Binaries/Win64/Client-Win64-Shipping.exe", "md5": "aaaa1111aaaa1111aaaa1111aaaa1111", "size": 10 },
                { "dest": "Client/Content/Paks/pakchunk 7.pak", "md5": "bbbb2222bbbb2222bbbb2222bbbb2222", "size": 20,
                  "chunkInfos": [ { "start": 0, "end": 9, "md5": "cccc3333cccc3333cccc3333cccc3333" } ] },
                { "dest": "Extra/file.dat", "md5": "dddd4444dddd4444dddd4444dddd4444", "size": 30, "fromFolder": "resources/extra/" }
              ]
            }
            """;

        var predownload = includePredownload
            ? $$"""
              ,
              "predownload": {
                "version": "3.7.0",
                "resourcesBasePath": "launcher/game/G152/10003/3.7.0/token/zip/",
                "config": {
                  "version": "3.7.0",
                  "patchConfig": [ { "version": "3.6.0", "indexFile": "resource/370/indexFile.json", "indexFileMd5": "ee00ee00ee00ee00ee00ee00ee00ee00" } ]
                }
              },
              "predownloadSwitch": {{predownloadSwitch}}
              """
            : "";

        var indexJson = $$"""
            {
              "default": {
                "version": "3.6.0",
                "cdnList": [
                  { "P": 1,  "K1": 1, "K2": 1, "url": "https://cdn-b.example.com/" },
                  { "P": 10, "K1": 1, "K2": 1, "url": "{{Cdn}}" },
                  { "P": 99, "K1": 0, "K2": 1, "url": "https://cdn-c.example.com/" }
                ],
                "resourcesBasePath": "launcher/game/G152/10003/3.6.0/token/zip/",
                "config": {
                  "version": "3.6.0",
                  "indexFile": "resource/10003/3.6.0/token/indexFile.json",
                  "indexFileMd5": "{{Md5(indexFileJson)}}",
                  "baseUrl": "launcher/game/G152/10003/3.6.0/token/zip/",
                  "patchConfig": [
                    { "version": "3.5.0", "indexFile": "resource/10003/3.6.0/350/indexFile.json", "indexFileMd5": "ff11ff11ff11ff11ff11ff11ff11ff11", "baseUrl": "launcher/game/G152/10003/3.6.0/patch350/" },
                    { "version": "3.4.0", "indexFile": "resource/10003/3.6.0/340/indexFile.json", "indexFileMd5": "ff22ff22ff22ff22ff22ff22ff22ff22" }
                  ]
                }
              }{{predownload}}
            }
            """;

        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/10003/3.6.0/token/indexFile.json", indexFileJson);

        return (indexJson, indexFileJson);
    }

    private static string Md5(string s) => Hashing.Md5Hex(System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task GetManifest_NullDestEntry_SkippedInsteadOfThrowing()
    {
        // F42（artifacts/bugs.md）：dest 显式 null 覆盖 ="" 初始化器（STJ null-over-initializer），
        // KuroUrlBuilder.Build 裸解引用 NRE 穿出渠道 → 单条畸形炸全量更新、分类 Unknown。
        // 防线：dest 空白的条目跳过，其余条目保留（与 ParsePage time/name 缺失 continue 同语义）
        var indexFileJson = """
            {
              "resource": [
                { "dest": "Client/Content/Paks/keep1.pak", "md5": "aaaa1111aaaa1111aaaa1111aaaa1111", "size": 10 },
                { "dest": null, "md5": "bbbb2222bbbb2222bbbb2222bbbb2222", "size": 20 },
                { "dest": "Client/Content/Paks/keep2.pak", "md5": "cccc3333cccc3333cccc3333cccc3333", "size": 30 }
              ]
            }
            """;
        var indexJson = $$"""
            {
              "default": {
                "version": "3.6.0",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.6.0/token/zip/",
                "config": {
                  "version": "3.6.0",
                  "indexFile": "resource/10003/3.6.0/token/indexFile.json",
                  "indexFileMd5": "{{Md5(indexFileJson)}}"
                }
              }
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/10003/3.6.0/token/indexFile.json", indexFileJson);

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.6.0");

        Assert.Equal(
            ["Client/Content/Paks/keep1.pak", "Client/Content/Paks/keep2.pak"],
            [.. manifest.Files.Select(f => f.Path)]);
    }

    [Fact]
    public async Task GetIncrementalManifest_NullDestGroupOrEntry_SkippedInsteadOfThrowing()
    {
        // F42 同族：差分组 dest null → ToGroups/BuildPatchUrl NRE；组内 srcFiles 条目 dest null → ToManifestFiles NRE
        RegisterFullFixture();
        const string patchIndexFile = """
            {
              "resource": [
                { "dest": "Client/Content/Paks/brand-new.pak", "md5": "12341234123412341234123412341234", "size": 40 }
              ],
              "groupInfos": [
                {
                  "dest": null, "size": 2048, "md5": "56785678567856785678567856785678",
                  "srcFiles": [], "dstFiles": []
                },
                {
                  "dest": "patch_ok.krpdiff", "size": 128, "md5": "abababababababababababababababab",
                  "srcFiles": [ { "dest": null, "md5": "99999999999999999999999999999999", "size": 4 },
                                { "dest": "Client/Content/Paks/old.pak", "md5": "88888888888888888888888888888888", "size": 4 } ],
                  "dstFiles": [ { "dest": "Client/Content/Paks/old.pak", "md5": "12121212121212121212121212121212", "size": 5 } ]
                }
              ]
            }
            """;
        _downloader.Serve(Cdn + "resource/10003/3.6.0/350/indexFile.json", patchIndexFile);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.5.0", "3.6.0");

        Assert.NotNull(manifest);
        var group = Assert.Single(manifest.Groups);
        Assert.Equal("patch_ok.krpdiff", group.PatchFile);
        Assert.Equal("Client/Content/Paks/old.pak", Assert.Single(group.SrcFiles).Path);
    }

    [Fact]
    public async Task GetVersionInfo_ParsesVersionPatchSourcesAndPredownload()
    {
        RegisterFullFixture();

        var info = await CreateApi().GetVersionInfoAsync(Server());

        Assert.Equal("3.6.0", info.LatestVersion);
        Assert.Equal(["3.5.0", "3.4.0"], info.PatchSourceVersions);
        Assert.True(info.PredownloadAvailable);
        Assert.Equal("3.7.0", info.PredownloadVersion);
        Assert.Equal(["3.6.0"], info.PredownloadPatchSourceVersions);
    }

    [Fact]
    public async Task GetVersionInfo_PredownloadClosed_WhenBlockMissing()
    {
        RegisterFullFixture(includePredownload: false);

        var info = await CreateApi().GetVersionInfoAsync(Server());

        Assert.False(info.PredownloadAvailable);
        Assert.Null(info.PredownloadVersion);
    }

    [Fact]
    public async Task GetVersionInfo_PredownloadClosed_WhenSwitchOff()
    {
        // 回归（2026-09-20）：官方契约是 predownloadSwitch 且存在 predownload.config 双条件；
        // 官方关开关但块残留时不得误报可预下载（ChannelVersionInfo 注释一直如此承诺）
        RegisterFullFixture(predownloadSwitch: "0");

        var info = await CreateApi().GetVersionInfoAsync(Server());

        Assert.False(info.PredownloadAvailable);
        Assert.Equal("3.7.0", info.PredownloadVersion);
    }

    [Fact]
    public async Task GetVersionInfo_PredownloadClosed_WhenSwitchMissing()
    {
        RegisterFullFixture(predownloadSwitch: "null");

        var info = await CreateApi().GetVersionInfoAsync(Server());

        Assert.False(info.PredownloadAvailable);
    }

    [Fact]
    public async Task GetVersionInfo_MissingIndexUrlOption_Throws()
    {
        var server = new GameServer { Id = "x", Name = "X" };

        await Assert.ThrowsAsync<UpdateException>(
            () => CreateApi().GetVersionInfoAsync(server));
    }

    [Fact]
    public async Task GetManifest_BuildsFilesWithCdnUrlsAndEscaping()
    {
        RegisterFullFixture();

        var manifest = await CreateApi().GetManifestAsync(Server(), "3.6.0");

        Assert.Equal("3.6.0", manifest.Version);
        Assert.Equal(3, manifest.Files.Count);

        var exe = manifest.Files[0];
        Assert.Equal("Client/Binaries/Win64/Client-Win64-Shipping.exe", exe.Path);
        Assert.Equal(Cdn + "launcher/game/G152/10003/3.6.0/token/zip/Client/Binaries/Win64/Client-Win64-Shipping.exe", exe.Url);

        // 空格转义
        var pak = manifest.Files[1];
        Assert.EndsWith("pakchunk%207.pak", pak.Url);

        // fromFolder 覆盖默认资源目录
        var extra = manifest.Files[2];
        Assert.Equal(Cdn + "resources/extra/Extra/file.dat", extra.Url);
    }

    [Fact]
    public async Task GetManifest_IndexFileMd5Mismatch_ThrowsVerificationException()
    {
        // 用真实下载器 + 桩 HTTP，验证清单 MD5 防篡改链路端到端生效
        var (indexJson, _) = RegisterFullFixture();
        var handler = new StubHttpHandler();
        handler.Map(Server().Options["indexUrl"], indexJson);
        handler.Map(Cdn + "resource/10003/3.6.0/token/indexFile.json", "{ \"resource\": [] }");
        var api = new KuroChannelApi(new HttpFileDownloader(
            new HttpClient(handler), new HttpFileDownloaderOptions { MaxAttempts = 1 }));

        await Assert.ThrowsAsync<DownloadVerificationException>(
            () => api.GetManifestAsync(Server(), "3.6.0"));
    }

    [Fact]
    public async Task GetIncrementalManifest_MatchesExactSourceVersion()
    {
        RegisterFullFixture();
        var patchIndexFile = """
            {
              "resource": [
                { "dest": "Client/Content/Paks/brand-new.pak", "md5": "12341234123412341234123412341234", "size": 40 }
              ],
              "groupInfos": [
                {
                  "dest": "patch_350_360.krpdiff",
                  "size": 2048,
                  "md5": "56785678567856785678567856785678",
                  "srcFiles": [ { "dest": "Client/Content/Paks/old.pak", "md5": "99999999999999999999999999999999", "size": 4 } ],
                  "dstFiles": [ { "dest": "Client/Content/Paks/old.pak", "md5": "12121212121212121212121212121212", "size": 5 } ]
                }
              ]
            }
            """;
        _downloader.Serve(Cdn + "resource/10003/3.6.0/350/indexFile.json", patchIndexFile);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.5.0", "3.6.0");

        Assert.NotNull(manifest);
        Assert.Equal("3.6.0", manifest.Version);

        // 增量清单的 resource（可直接完整下载的新文件）指向差分入口的 baseUrl
        Assert.Equal(
            Cdn + "launcher/game/G152/10003/3.6.0/patch350/Client/Content/Paks/brand-new.pak",
            manifest.Files[0].Url);

        var group = Assert.Single(manifest.Groups);
        Assert.Equal("patch_350_360.krpdiff", group.PatchFile);
        Assert.Equal(2048, group.PatchSize);
        Assert.Equal(
            Cdn + "launcher/game/G152/10003/3.6.0/patch350/patch_350_360.krpdiff",
            group.Url);
        Assert.Equal("Client/Content/Paks/old.pak", Assert.Single(group.SrcFiles).Path);
        Assert.Null(Assert.Single(group.SrcFiles).Url);
    }

    [Fact]
    public async Task GetIncrementalManifest_PredownloadSourceVersion_UsesPredownloadBlock()
    {
        // 回归（2026-09-20）：预下载差分入口（live → predownload）在 predownload 块的 patchConfig 里，
        // 旧实现只查 default 块——预下载窗口期本地版本命中 PredownloadPatchSourceVersions 时必然失败。
        // 本 fixture 的 predownload 块携带 3.6.0 → 3.7.0 的差分入口，default 块没有该条目。
        RegisterFullFixture();
        const string patchIndexFile = """
            {
              "resource": [
                { "dest": "Client/Content/Paks/predownload.pak", "md5": "12341234123412341234123412341234", "size": 40 }
              ]
            }
            """;
        _downloader.Serve(Cdn + "resource/370/indexFile.json", patchIndexFile);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.6.0", "3.7.0");

        Assert.NotNull(manifest);
        Assert.Equal("3.7.0", manifest.Version);

        // 资源目录按 predownload 块自己的 baseUrl/resourcesBasePath 解析
        Assert.Equal(
            Cdn + "launcher/game/G152/10003/3.7.0/token/zip/Client/Content/Paks/predownload.pak",
            manifest.Files[0].Url);
    }

    [Fact]
    public async Task GetIncrementalManifest_PredownloadBlockWithoutCdnList_FallsBackToDefaultCdn()
    {
        // 回归（2026-09-29 真机实测，CN 服 3.7.0 预载窗口）：官方 index.json 的 predownload 块
        // 不携带 cdnList（仅 changelog/config/resources/resourcesBasePath/version 五键），cdnList
        // 只随 default 块下发、两块共用；旧实现 RequireCdn(选中块) 对 predownload 块抛
        // "cdnList has no usable node"，预载窗口期点「预下载」必失败。fixture 对齐真机形态。
        const string patchIndexFile = """
            {
              "resource": [
                { "dest": "Client/Content/Paks/predownload.pak", "md5": "12341234123412341234123412341234", "size": 40 }
              ]
            }
            """;
        var indexJson = $$"""
            {
              "default": {
                "version": "3.6.1",
                "cdnList": [ { "P": 10, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.6.1/token/zip/",
                "config": { "version": "3.6.1" }
              },
              "predownload": {
                "version": "3.7.0",
                "resourcesBasePath": "launcher/game/G152/10003/3.7.0/token/zip/",
                "config": {
                  "version": "3.7.0",
                  "patchConfig": [ { "version": "3.6.1", "indexFile": "resource/370/indexFile.json", "indexFileMd5": "{{Md5(patchIndexFile)}}" } ]
                }
              },
              "predownloadSwitch": 1
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/370/indexFile.json", patchIndexFile);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.6.1", "3.7.0");

        Assert.NotNull(manifest);
        Assert.Equal("3.7.0", manifest.Version);
        // CDN 回退自 default 块；差分入口与资源目录解析自 predownload 块
        Assert.Equal(
            Cdn + "launcher/game/G152/10003/3.7.0/token/zip/Client/Content/Paks/predownload.pak",
            manifest.Files[0].Url);
    }

    [Fact]
    public async Task GetIncrementalManifest_PredownloadBlockWithOwnCdnList_PrefersIt()
    {
        // 回退语义的另一半：predownload 块自带可用 cdnList 时优先自有节点、不落 default——
        // 真机 2026-09-29 只随 default 下发，但按"自有优先、缺失回退"双向兼容官方形态变化
        const string patchIndexFile = """
            {
              "resource": [
                { "dest": "Client/Content/Paks/predownload.pak", "md5": "12341234123412341234123412341234", "size": 40 }
              ]
            }
            """;
        var indexJson = $$"""
            {
              "default": {
                "version": "3.6.1",
                "cdnList": [ { "P": 10, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.6.1/token/zip/",
                "config": { "version": "3.6.1" }
              },
              "predownload": {
                "version": "3.7.0",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "https://cdn-pre.example.com/" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.7.0/token/zip/",
                "config": {
                  "version": "3.7.0",
                  "patchConfig": [ { "version": "3.6.1", "indexFile": "resource/370/indexFile.json", "indexFileMd5": "{{Md5(patchIndexFile)}}" } ]
                }
              },
              "predownloadSwitch": 1
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve("https://cdn-pre.example.com/resource/370/indexFile.json", patchIndexFile);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.6.1", "3.7.0");

        Assert.NotNull(manifest);
        Assert.StartsWith("https://cdn-pre.example.com/", manifest.Files[0].Url);
    }

    [Fact]
    public async Task GetIncrementalManifest_ResourceWithoutFromFolder_FallsBackToFirstFromFolder()
    {
        // 回归（2026-09-29 真机实测，CN 服 3.7.0/3.6.1 增量清单 + 参考实现 ww-manager
        // incremental.py 语义）：清单 resource 列表常带少量 fromFolder 条目指向目标版本
        // zip/ 目录（真机 11 条），无 fromFolder 的条目共用该前缀；patchEntry.baseUrl
        // 是差分包目录（真机 .../3.6.1/resources/ 三个 CDN 节点全 404，zip/ 206），
        // 作资源前缀时排在「列表第一个 fromFolder」之后。
        const string patchIndexFile = """
            {
              "resource": [
                { "dest": "Client/Content/Paks/with-folder.pak", "md5": "11111111111111111111111111111111", "size": 10,
                  "fromFolder": "launcher/game/G152/10003/3.7.0/token/zip/" },
                { "dest": "Client/Content/Paks/plain.pak", "md5": "22222222222222222222222222222222", "size": 20 }
              ]
            }
            """;
        var indexJson = $$"""
            {
              "default": {
                "version": "3.6.1",
                "cdnList": [ { "P": 10, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.6.1/token/zip/",
                "config": { "version": "3.6.1" }
              },
              "predownload": {
                "version": "3.7.0",
                "resourcesBasePath": "launcher/game/G152/10003/3.7.0/token/zip/",
                "config": {
                  "version": "3.7.0",
                  "patchConfig": [ { "version": "3.6.1", "indexFile": "resource/370/indexFile.json", "indexFileMd5": "{{Md5(patchIndexFile)}}",
                                     "baseUrl": "launcher/game/G152/10003/3.7.0/token/resource/3.6.1/resources/" } ]
                }
              },
              "predownloadSwitch": 1
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/370/indexFile.json", patchIndexFile);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.6.1", "3.7.0");

        Assert.NotNull(manifest);
        // 带 fromFolder 的条目用自身目录（既有语义不变）
        Assert.Equal(
            Cdn + "launcher/game/G152/10003/3.7.0/token/zip/Client/Content/Paks/with-folder.pak",
            manifest.Files[0].Url);
        // 无 fromFolder 的条目回退「列表第一个 fromFolder」，不得指向差分包目录 resources/
        Assert.Equal(
            Cdn + "launcher/game/G152/10003/3.7.0/token/zip/Client/Content/Paks/plain.pak",
            manifest.Files[1].Url);
    }

    [Fact]
    public async Task GetIncrementalManifest_KrpdiffEntries_ExcludedFromFilesAndBackfilledIntoGroups()
    {
        // 回归（2026-10-02 真机实测，CN 服 3.6.1→3.7.0 转正后增量清单 + 参考实现 ww-manager
        // incremental.py 语义互证）：清单 resource 列表同时登记「直下文件（带 fromFolder 指向
        // 目标版本 zip/）」与「krpdiff 差分文件（无 fromFolder，与 groupInfos[].dest 一一同名）」。
        // krpdiff 必须只经 Groups 表达（URL = patchEntry.baseUrl 差分目录，真机 38/38 全 206），
        // 不得留在 Files——旧实现给它套 fromFolder 前缀（zip/）实测 38/38 全 404（用户报障的
        // 404 URL 即 Files 循环产物），且会同文件下载两遍、apply 时被搬进游戏目录。
        // size/md5：组层无此键（真机实测），从 resource[] 同名条目回填，使下载校验与进度总量可用。
        const string patchIndexFile = """
            {
              "resource": [
                { "dest": "Client/Binaries/Win64/Client-Win64-Shipping.exe", "md5": "11111111111111111111111111111111", "size": 10,
                  "fromFolder": "launcher/game/G152/10003/3.7.0/token/zip/" },
                { "dest": "3.6.1_3.7.0_group_0_token.krpdiff", "md5": "22222222222222222222222222222222", "size": 100 },
                { "dest": "3.6.1_3.7.0_group_1_token.krpdiff", "md5": "33333333333333333333333333333333", "size": 200 }
              ],
              "groupInfos": [
                {
                  "dest": "3.6.1_3.7.0_group_0_token.krpdiff",
                  "srcFiles": [ { "dest": "Client/Content/Paks/old0.pak", "md5": "99999999999999999999999999999999", "size": 4 } ],
                  "dstFiles": [ { "dest": "Client/Content/Paks/new0.pak", "md5": "12121212121212121212121212121212", "size": 5 } ]
                },
                {
                  "dest": "3.6.1_3.7.0_group_1_token.krpdiff",
                  "srcFiles": [ { "dest": "Client/Content/Paks/old1.pak", "md5": "88888888888888888888888888888888", "size": 4 } ],
                  "dstFiles": [ { "dest": "Client/Content/Paks/new1.pak", "md5": "13131313131313131313131313131313", "size": 5 } ]
                }
              ]
            }
            """;
        var indexJson = $$"""
            {
              "default": {
                "version": "3.7.0",
                "cdnList": [ { "P": 10, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.7.0/token/zip/",
                "config": {
                  "version": "3.7.0",
                  "patchConfig": [ { "version": "3.6.1", "indexFile": "resource/370/indexFile.json", "indexFileMd5": "{{Md5(patchIndexFile)}}",
                                     "baseUrl": "launcher/game/G152/10003/3.7.0/token/resource/10003/3.7.0/3.6.1/resources/" } ]
                }
              },
              "predownloadSwitch": 1
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/370/indexFile.json", patchIndexFile);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.6.1", "3.7.0");

        Assert.NotNull(manifest);
        // Files 只保留直下文件（krpdiff 不得重复登记为安装文件）
        var file = Assert.Single(manifest.Files);
        Assert.Equal("Client/Binaries/Win64/Client-Win64-Shipping.exe", file.Path);
        Assert.Equal(Cdn + "launcher/game/G152/10003/3.7.0/token/zip/Client/Binaries/Win64/Client-Win64-Shipping.exe", file.Url);

        // Groups 表达全部 krpdiff：URL 走差分目录（patchEntry.baseUrl），size/md5 自 resource[] 同名条目回填
        Assert.Equal(2, manifest.Groups.Count);
        Assert.Equal("3.6.1_3.7.0_group_0_token.krpdiff", manifest.Groups[0].PatchFile);
        Assert.Equal(100, manifest.Groups[0].PatchSize);
        Assert.Equal("22222222222222222222222222222222", manifest.Groups[0].PatchMd5);
        Assert.Equal(
            Cdn + "launcher/game/G152/10003/3.7.0/token/resource/10003/3.7.0/3.6.1/resources/3.6.1_3.7.0_group_0_token.krpdiff",
            manifest.Groups[0].Url);
        Assert.Equal(200, manifest.Groups[1].PatchSize);
        Assert.Equal("33333333333333333333333333333333", manifest.Groups[1].PatchMd5);
        // 官方 deleteFiles（顶层废弃文件清单）透传进 GameManifest 供 Apply 前删除
        Assert.NotNull(manifest.DeleteFiles);
    }

    [Fact]
    public async Task GetIncrementalManifest_DeleteFiles_PassedThrough()
    {
        // 2026-10-02 真机实测：增量清单顶层 deleteFiles（6 条旧 pak/sig）——残留会被 UE 挂载
        // 覆盖新文件、热更卡死；渠道必须透传给 Core 的 Apply 阶段（ww-manager 同语义）。
        const string patchIndexFile = """
            {
              "resource": [],
              "deleteFiles": [
                "Client/Content/Paks/pakchunk27-WindowsNoEditor.pak",
                "Client/Content/Paks/pakchunk27-WindowsNoEditor.sig"
              ],
              "groupInfos": []
            }
            """;
        var indexJson = $$"""
            {
              "default": {
                "version": "3.7.0",
                "cdnList": [ { "P": 10, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "config": {
                  "version": "3.7.0",
                  "patchConfig": [ { "version": "3.6.1", "indexFile": "resource/370/indexFile.json", "indexFileMd5": "{{Md5(patchIndexFile)}}",
                                     "baseUrl": "launcher/game/G152/10003/3.7.0/token/resources/" } ]
                }
              }
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/370/indexFile.json", patchIndexFile);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.6.1", "3.7.0");

        Assert.NotNull(manifest);
        Assert.Equal(
            ["Client/Content/Paks/pakchunk27-WindowsNoEditor.pak", "Client/Content/Paks/pakchunk27-WindowsNoEditor.sig"],
            [.. manifest.DeleteFiles]);
    }

    [Fact]
    public async Task GetIncrementalManifest_DuplicateResourceDest_DoesNotThrow()
    {
        // RF-1（2026-10-02 review）：resource[] 含重复 dest 时回填字典不得抛
        // ArgumentException（Dictionary 构造器对重复键的确定行为）——清单不可信（F42 同族），
        // 单条畸形不得炸整个增量清单获取、落分类 Unknown。
        const string patchIndexFile = """
            {
              "resource": [
                { "dest": "3.6.1_3.7.0_group_0_token.krpdiff", "md5": "22222222222222222222222222222222", "size": 100 },
                { "dest": "3.6.1_3.7.0_group_0_token.krpdiff", "md5": "33333333333333333333333333333333", "size": 200 },
                { "dest": "Client/direct.pak", "md5": "11111111111111111111111111111111", "size": 10 }
              ],
              "groupInfos": [
                {
                  "dest": "3.6.1_3.7.0_group_0_token.krpdiff",
                  "srcFiles": [ { "dest": "old.pak", "md5": "99999999999999999999999999999999", "size": 4 } ],
                  "dstFiles": [ { "dest": "new.pak", "md5": "12121212121212121212121212121212", "size": 5 } ]
                }
              ]
            }
            """;
        var indexJson = $$"""
            {
              "default": {
                "version": "3.7.0",
                "cdnList": [ { "P": 10, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "config": {
                  "version": "3.7.0",
                  "patchConfig": [ { "version": "3.6.1", "indexFile": "resource/370/indexFile.json", "indexFileMd5": "{{Md5(patchIndexFile)}}",
                                     "baseUrl": "launcher/game/G152/10003/3.7.0/token/resources/" } ]
                }
              }
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/370/indexFile.json", patchIndexFile);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.6.1", "3.7.0");

        Assert.NotNull(manifest);
        // 先登记者胜（与 ToManifestFiles 顺序处理语义一致）；任一条目被采即可，不抛是底线
        var group = Assert.Single(manifest.Groups);
        Assert.Equal(100, group.PatchSize);
        Assert.Single(manifest.Files);
    }

    [Fact]
    public async Task GetIncrementalManifest_RegularIncrement_NoGroupInfos_KeepsAllDirectFiles()
    {
        // 守护（2026-10-02）：常规增量清单（真机 3.6.0→3.6.1 形态——无 groupInfos、无 fromFolder、
        // patchEntry.baseUrl=zip/ 资源目录）不含 krpdiff，过滤逻辑不得误伤：直下文件全部保留、
        // 前缀落 patchEntry.baseUrl（真机 173 条直下全此形态，R-F83-1 实测 zip/ 206）。
        const string patchIndexFile = """
            {
              "resource": [
                { "dest": "Client/Content/Paks/a.pak", "md5": "11111111111111111111111111111111", "size": 10 },
                { "dest": "Client/Content/Paks/b.pak", "md5": "22222222222222222222222222222222", "size": 20 }
              ]
            }
            """;
        var indexJson = $$"""
            {
              "default": {
                "version": "3.6.1",
                "cdnList": [ { "P": 10, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.6.1/token/zip/",
                "config": {
                  "version": "3.6.1",
                  "patchConfig": [ { "version": "3.6.0", "indexFile": "patch/361/indexFile.json", "indexFileMd5": "{{Md5(patchIndexFile)}}",
                                     "baseUrl": "launcher/game/G152/10003/3.6.1/token/zip/" } ]
                }
              }
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "patch/361/indexFile.json", patchIndexFile);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.6.0", "3.6.1");

        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.Files.Count);
        Assert.All(manifest.Files, f => Assert.StartsWith(Cdn + "launcher/game/G152/10003/3.6.1/token/zip/", f.Url));
        Assert.Empty(manifest.Groups);
    }

    [Fact]
    public async Task GetIncrementalManifest_PredownloadBlockWithUnusableCdnList_FallsBackToDefaultCdn()
    {
        // R-TEST-1（2026-09-29 review 立案）：组合中间态——predownload 块带 cdnList 但全节点
        // 不可用（K1≠1）与「无 cdnList」同样落 default 回退；两端用例（块缺失 / 可用自有）不覆盖此态
        const string patchIndexFile = """
            {
              "resource": [
                { "dest": "Client/Content/Paks/predownload.pak", "md5": "12341234123412341234123412341234", "size": 40 }
              ]
            }
            """;
        var indexJson = $$"""
            {
              "default": {
                "version": "3.6.1",
                "cdnList": [ { "P": 10, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.6.1/token/zip/",
                "config": { "version": "3.6.1" }
              },
              "predownload": {
                "version": "3.7.0",
                "cdnList": [ { "P": 10, "K1": 0, "K2": 1, "url": "https://cdn-pre.example.com/" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.7.0/token/zip/",
                "config": {
                  "version": "3.7.0",
                  "patchConfig": [ { "version": "3.6.1", "indexFile": "resource/370/indexFile.json", "indexFileMd5": "{{Md5(patchIndexFile)}}" } ]
                }
              },
              "predownloadSwitch": 1
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "resource/370/indexFile.json", patchIndexFile);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.6.1", "3.7.0");

        Assert.NotNull(manifest);
        // 自有节点全不可用（K1=0），CDN 取自 default 块而非放行不可用节点
        Assert.StartsWith(Cdn, manifest.Files[0].Url);
    }

    [Fact]
    public async Task GetIncrementalManifest_FallsBackToOtherBlock_WhenSelectedBlockLacksEntry()
    {
        // 回归（2026-09-20 复审）：官方切版本窗口期差分条目与目标版本可能不同块——
        // default 已切到 3.7.0、predownload 块残留同版本但其 patchConfig 已被 CDN 清理。
        // 3.6.0 → 3.7.0 的常规增量选中 predownload 块后查不到条目，必须回退 default 块
        // 找到差分入口，而不是返回 null 把可用增量推向全量重下。
        const string patchIndexFile = """
            {
              "resource": [
                { "dest": "Client/Content/Paks/updated.pak", "md5": "56785678567856785678567856785678", "size": 40 }
              ]
            }
            """;
        var indexJson = $$"""
            {
              "default": {
                "version": "3.7.0",
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.7.0/token/zip/",
                "config": {
                  "version": "3.7.0",
                  "patchConfig": [ { "version": "3.6.0", "indexFile": "patch/360/indexFile.json", "indexFileMd5": "{{Md5(patchIndexFile)}}", "baseUrl": "patch360/" } ]
                }
              },
              "predownload": {
                "version": "3.7.0",
                "cdnList": [ { "P": 10, "K1": 1, "K2": 1, "url": "https://cdn-pre.example.com/" } ],
                "resourcesBasePath": "pre/370/",
                "config": { "version": "3.7.0" }
              },
              "predownloadSwitch": 1
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);
        _downloader.Serve(Cdn + "patch/360/indexFile.json", patchIndexFile);

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "3.6.0", "3.7.0");

        Assert.NotNull(manifest);
        Assert.Equal("3.7.0", manifest.Version);
        // 条目解析自回退命中的 default 块（CDN 与 baseUrl 都是 default 块的）
        Assert.Equal(Cdn + "patch360/Client/Content/Paks/updated.pak", manifest.Files[0].Url);
    }

    [Fact]
    public async Task GetVersionInfo_MissingVersion_IsRejected()
    {
        // 回归（2026-09-20 复审）：版本缺失（config 与块级都无 version）拒收而非登记空串——
        // 空版本落盘后 IsNewer 恒判"无更新"，游戏永久失去更新检测且无自愈路径
        const string indexJson = $$"""
            {
              "default": {
                "cdnList": [ { "P": 1, "K1": 1, "K2": 1, "url": "{{Cdn}}" } ],
                "resourcesBasePath": "launcher/game/G152/10003/3.6.0/token/zip/"
              }
            }
            """;
        _downloader.Serve(Server().Options["indexUrl"], indexJson);

        await Assert.ThrowsAsync<UpdateException>(() => CreateApi().GetVersionInfoAsync(Server()));
    }

    [Fact]
    public async Task GetIncrementalManifest_UnknownSourceVersion_ReturnsNull()
    {
        RegisterFullFixture();

        var manifest = await CreateApi().GetIncrementalManifestAsync(Server(), "2.0.0", "3.6.0");

        Assert.Null(manifest);
    }

    [Fact]
    public void CdnSelector_PicksHighestPriorityWithK1K2()
    {
        var nodes = new List<Models.KuroCdnNode>
        {
            new() { Priority = 1, K1 = 1, K2 = 1, Url = "https://a/" },
            new() { Priority = 10, K1 = 1, K2 = 1, Url = "https://b/" },
            new() { Priority = 99, K1 = 0, K2 = 1, Url = "https://c/" },
        };

        Assert.Equal("https://b/", KuroCdnSelector.SelectCdn(nodes));
    }

    [Fact]
    public void PatchUrl_FallsBackToDefaultBaseUrlThenResources()
    {
        Assert.Equal(
            "https://cdn/patch350/x.krpdiff",
            KuroUrlBuilder.BuildPatchUrl("https://cdn/", "patch350/", "default/", "x.krpdiff"));
        Assert.Equal(
            "https://cdn/default/x.krpdiff",
            KuroUrlBuilder.BuildPatchUrl("https://cdn/", null, "default/", "x.krpdiff"));
        Assert.Equal(
            "https://cdn/resources/x.krpdiff",
            KuroUrlBuilder.BuildPatchUrl("https://cdn/", null, null, "x.krpdiff"));
    }
}
