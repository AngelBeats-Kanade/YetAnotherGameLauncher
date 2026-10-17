using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

[Collection("sequential")]
public class GameUpdateServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();
    private readonly FakePatchApplier _applier = new();
    private readonly FakeChannel _channel = new();
    private readonly GameServer _server = new() { Id = "cn", Name = "国服" };
    private readonly GameDefinition _game;

    public GameUpdateServiceTests()
    {
        _game = new GameDefinition
        {
            Id = "test-game",
            DisplayName = "测试游戏",
            Channel = "kuro",
            InstallDir = "TestGame",
            Executable = "game.exe",
            Servers = [_server],
        };
    }

    public void Dispose() => _tempDir.Dispose();

    private static string Md5(byte[] data) => Hashing.Md5Hex(data);

    private static string Url(string name) => $"https://cdn.example.com/{name}";

    private static ManifestFile FileEntry(string path, byte[] content) =>
        new(path, content.Length, Md5(content), Url: Url(path.Replace('/', '_')));

    private GameUpdateService CreateService() => new(_downloader, _applier);

    private PatchGroup BuildGroup(
        string patchName,
        (string Path, byte[] Content)[] srcs,
        (string Path, byte[] Content)[] dsts)
    {
        var patchContent = System.Text.Encoding.UTF8.GetBytes($"patch::{patchName}");
        _downloader.Responses[Url(patchName)] = patchContent;
        return new PatchGroup(
            patchName,
            patchContent.Length,
            Md5(patchContent),
            [.. srcs.Select(s => FileEntry(s.Path, s.Content))],
            [.. dsts.Select(d => FileEntry(d.Path, d.Content))],
            Url(patchName));
    }

    private void RegisterFile(string path, byte[] content) =>
        _downloader.Responses[Url(path.Replace('/', '_'))] = content;

    private async Task WriteLocalState(string version) =>
        await new LocalStateService(_tempDir.Path).SaveAsync(
            new LocalGameState { GameId = _game.Id, ServerId = _server.Id, Version = version });

    // ---------- 全量同步路径 ----------

    [Fact]
    public async Task UpdateAsync_NoLocalVersion_RunsFullSyncAndSavesState()
    {
        var a = "aaa"u8.ToArray();
        RegisterFile("a.dat", a);
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0", PatchSourceVersions = ["1.0.0"] };
        _channel.Manifests["2.0.0"] = new GameManifest { Version = "2.0.0", Files = [FileEntry("a.dat", a)] };

        var outcome = await CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal(UpdateStrategy.FullSync, outcome.Strategy);
        Assert.Equal("2.0.0", outcome.ToVersion);
        Assert.Equal(a, await File.ReadAllBytesAsync(_tempDir.FilePath("a.dat")));
        Assert.Equal("2.0.0", new LocalStateService(_tempDir.Path).Load(_game.Id, _server.Id)?.Version);
    }

    [Fact]
    public async Task UpdateAsync_StateBelongsToOtherGame_TreatedAsNotInstalled()
    {
        var a = "aaa"u8.ToArray();
        RegisterFile("a.dat", a);
        await new LocalStateService(_tempDir.Path).SaveAsync(
            new LocalGameState { GameId = "other-game", ServerId = _server.Id, Version = "9.9.9" });
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" };
        _channel.Manifests["2.0.0"] = new GameManifest { Version = "2.0.0", Files = [FileEntry("a.dat", a)] };

        var outcome = await CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal(UpdateStrategy.FullSync, outcome.Strategy);
    }

    // ---------- 增量路径 ----------

    [Fact]
    public async Task UpdateAsync_LocalVersionInPatches_RunsIncremental()
    {
        var v1 = "v1"u8.ToArray();
        var v2 = "v2"u8.ToArray();
        var newFile = "brand-new"u8.ToArray();
        await File.WriteAllBytesAsync(_tempDir.FilePath("a.dat"), v1);
        await WriteLocalState("1.0.0");

        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0", PatchSourceVersions = ["1.0.0"] };
        var group = BuildGroup("g1.krpdiff", [("a.dat", v1)], [("a.dat", v2)]);
        _applier.Outputs["g1.krpdiff"] = new Dictionary<string, byte[]> { ["a.dat"] = v2 };
        var incremental = new GameManifest
        {
            Version = "2.0.0",
            Groups = [group],
            Files = [FileEntry("b.pak", newFile)],
        };
        _channel.IncrementalManifests[("1.0.0", "2.0.0")] = incremental;
        _channel.Manifests["2.0.0"] = new GameManifest
        {
            Version = "2.0.0",
            Files = [FileEntry("a.dat", v2), FileEntry("b.pak", newFile)],
        };
        RegisterFile("b.pak", newFile);

        var outcome = await CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal(UpdateStrategy.Incremental, outcome.Strategy);
        Assert.Equal("1.0.0", outcome.FromVersion);
        Assert.Equal("2.0.0", outcome.ToVersion);
        Assert.Equal(0, outcome.RepairedFiles);
        Assert.Equal(v2, await File.ReadAllBytesAsync(_tempDir.FilePath("a.dat")));
        Assert.Equal(newFile, await File.ReadAllBytesAsync(_tempDir.FilePath("b.pak")));
        Assert.Equal("2.0.0", new LocalStateService(_tempDir.Path).Load(_game.Id, _server.Id)?.Version);
    }

    [Fact]
    public async Task UpdateAsync_Incremental_MissingNewFile_RepairedByFullManifest()
    {
        var v1 = "v1"u8.ToArray();
        var v2 = "v2"u8.ToArray();
        var b = "b-content"u8.ToArray();
        await File.WriteAllBytesAsync(_tempDir.FilePath("a.dat"), v1);
        await WriteLocalState("1.0.0");

        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0", PatchSourceVersions = ["1.0.0"] };
        var group = BuildGroup("g1.krpdiff", [("a.dat", v1)], [("a.dat", v2)]);
        _applier.Outputs["g1.krpdiff"] = new Dictionary<string, byte[]> { ["a.dat"] = v2 };
        // 增量清单不含 b.pak（未暂存），事后修复必须补下载
        _channel.IncrementalManifests[("1.0.0", "2.0.0")] = new GameManifest
        {
            Version = "2.0.0",
            Groups = [group],
        };
        _channel.Manifests["2.0.0"] = new GameManifest
        {
            Version = "2.0.0",
            Files = [FileEntry("a.dat", v2), FileEntry("b.pak", b)],
        };
        RegisterFile("b.pak", b);

        var outcome = await CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal(1, outcome.RepairedFiles);
        Assert.Contains(Url("b.pak"), _downloader.Requests);
        Assert.Equal(b, await File.ReadAllBytesAsync(_tempDir.FilePath("b.pak")));
    }

    [Fact]
    public async Task UpdateAsync_Incremental_MissingSources_FallsBackViaFullManifest()
    {
        // review RF-A（fe4324d 自身完整性缺口）：组级回退不能只接应用预下载入口——「更新」主按钮
        // 走 UpdateIncrementalAsync，用户重试损坏安装的最常见入口；差分源缺失时经全量清单解析
        // 产物直链自救，更新继续
        var v1 = "v1"u8.ToArray();
        var v2 = "v2"u8.ToArray();
        await WriteLocalState("1.0.0"); // 安装目录无 a.dat：源缺失（此前失败尝试删源的形态）

        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0", PatchSourceVersions = ["1.0.0"] };
        var group = BuildGroup("g1.krpdiff", [("a.dat", v1)], [("a.dat", v2)]);
        _channel.IncrementalManifests[("1.0.0", "2.0.0")] = new GameManifest { Version = "2.0.0", Groups = [group] };
        _channel.Manifests["2.0.0"] = new GameManifest { Version = "2.0.0", Files = [FileEntry("a.dat", v2)] };
        RegisterFile("a.dat", v2); // 全量清单直链可下

        var outcome = await CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal(UpdateStrategy.Incremental, outcome.Strategy);
        Assert.Equal(v2, await File.ReadAllBytesAsync(_tempDir.FilePath("a.dat")));
        Assert.Equal("2.0.0", new LocalStateService(_tempDir.Path).Load(_game.Id, _server.Id)?.Version);
    }

    [Fact]
    public async Task UpdateAsync_Incremental_MissingSources_FullManifestFetchFailure_FoldsToRootCause()
    {
        // review RF-B：全量清单抓取失败（CDN/网络故障）必须以根因折算（「Could not resolve a
        // direct download: …」）——不能被解析缝的内层 catch 吞成 null 走「查无此路径」的误导文案
        var v1 = "v1"u8.ToArray();
        var v2 = "v2"u8.ToArray();
        await WriteLocalState("1.0.0");
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0", PatchSourceVersions = ["1.0.0"] };
        var group = BuildGroup("g1.krpdiff", [("a.dat", v1)], [("a.dat", v2)]);
        _channel.IncrementalManifests[("1.0.0", "2.0.0")] = new GameManifest { Version = "2.0.0", Groups = [group] };
        _channel.ManifestError = new InvalidOperationException("cdn unreachable");

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel));

        Assert.Contains("Could not resolve a direct download", ex.Message);
        Assert.Contains("cdn unreachable", ex.Message);
    }

    // ---------- 预下载（两段式） ----------

    [Fact]
    public async Task PredownloadAsync_NoWindow_Throws()
    {
        await WriteLocalState("1.0.0");
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "1.0.0", PredownloadAvailable = false };

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().PredownloadAsync(_tempDir.Path, _game, _server, _channel));

        Assert.Contains("predownload", ex.Message);
    }

    [Fact]
    public async Task PredownloadAsync_NoMatchingPatch_ThrowsWithGuidance()
    {
        await WriteLocalState("1.0.0");
        _channel.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "1.0.0",
            PredownloadAvailable = true,
            PredownloadVersion = "2.0.0",
            PredownloadPatchSourceVersions = ["1.5.0"], // 与本地 1.0.0 不匹配
        };

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().PredownloadAsync(_tempDir.Path, _game, _server, _channel));

        Assert.Contains("has no patch for local version", ex.Message);
    }

    [Fact]
    public async Task PredownloadAsync_LocalAlreadyAtTarget_ShortCircuitsWithoutNetwork()
    {
        // F66：本地版本已等于预下载目标（预下载已应用/正式更新已到位而服务器窗口未关）时
        // 再触发预下载——原实现落 FullSync：包式渠道无条件整包重下数十 GB（Apply 后暂存已删、
        // IsArchiveIntact 无缓存可命中），Kuro 侧落"no patch for local version V2"自相矛盾文案。
        // 短路 = 幂等空操作：零清单拉取、零下载、暂存区不动
        await WriteLocalState("2.0.0");
        _channel.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "2.0.0",
            PredownloadAvailable = true,
            PredownloadVersion = "2.0.0",
        };

        var summary = await CreateService().PredownloadAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal("2.0.0", summary.FromVersion);
        Assert.Equal("2.0.0", summary.ToVersion);
        Assert.Equal(0, summary.TotalBytes);
        Assert.Empty(_channel.PredownloadManifestRequests);
        Assert.Empty(_downloader.Requests);
        Assert.Null(IncrementalUpdateService.TryLoadStagedManifest(_tempDir.Path));
    }

    [Fact]
    public async Task PredownloadAsync_Available_StagesIncrementalContent()
    {
        var v1 = "v1"u8.ToArray();
        var v2 = "v2"u8.ToArray();
        await File.WriteAllBytesAsync(_tempDir.FilePath("a.dat"), v1);
        await WriteLocalState("1.0.0");

        _channel.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "1.0.0",
            PredownloadAvailable = true,
            PredownloadVersion = "2.0.0",
            PredownloadPatchSourceVersions = ["1.0.0"],
        };
        var group = BuildGroup("g1.krpdiff", [("a.dat", v1)], [("a.dat", v2)]);
        var incremental = new GameManifest { Version = "2.0.0", Groups = [group] };
        _channel.IncrementalManifests[("1.0.0", "2.0.0")] = incremental;
        _downloader.Responses[Url("g1.krpdiff")] = "patch::g1.krpdiff"u8.ToArray();

        var summary = await CreateService().PredownloadAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal("2.0.0", summary.ToVersion);
        Assert.Equal(group.PatchSize, summary.TotalBytes);
        Assert.NotNull(IncrementalUpdateService.TryLoadStagedManifest(_tempDir.Path));
        // 预下载不改动游戏本体
        Assert.Equal(v1, await File.ReadAllBytesAsync(_tempDir.FilePath("a.dat")));
    }

    [Fact]
    public async Task ApplyPredownloadAsync_NothingStaged_Throws()
    {
        await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().ApplyPredownloadAsync(_tempDir.Path, _game, _server, _channel));
    }

    [Fact]
    public async Task ApplyPredownloadAsync_AppliesStagedAndSavesState()
    {
        var v1 = "v1"u8.ToArray();
        var v2 = "v2"u8.ToArray();
        await File.WriteAllBytesAsync(_tempDir.FilePath("a.dat"), v1);
        await WriteLocalState("1.0.0");

        _channel.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "1.0.0",
            PredownloadAvailable = true,
            PredownloadVersion = "2.0.0",
            PredownloadPatchSourceVersions = ["1.0.0"],
        };
        var group = BuildGroup("g1.krpdiff", [("a.dat", v1)], [("a.dat", v2)]);
        _applier.Outputs["g1.krpdiff"] = new Dictionary<string, byte[]> { ["a.dat"] = v2 };
        _channel.IncrementalManifests[("1.0.0", "2.0.0")] = new GameManifest { Version = "2.0.0", Groups = [group] };
        _channel.Manifests["2.0.0"] = new GameManifest { Version = "2.0.0", Files = [FileEntry("a.dat", v2)] };
        _downloader.Responses[Url("g1.krpdiff")] = "patch::g1.krpdiff"u8.ToArray();
        var service = CreateService();

        await service.PredownloadAsync(_tempDir.Path, _game, _server, _channel);
        var outcome = await service.ApplyPredownloadAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal(UpdateStrategy.Incremental, outcome.Strategy);
        Assert.Equal("2.0.0", outcome.ToVersion);
        Assert.Equal(v2, await File.ReadAllBytesAsync(_tempDir.FilePath("a.dat")));
        Assert.Equal("2.0.0", new LocalStateService(_tempDir.Path).Load(_game.Id, _server.Id)?.Version);
    }

    [Fact]
    public async Task ApplyPredownloadAsync_ServerLagsStagedVersion_SkipsRepair()
    {
        // 回归（2026-09-20）：窗口期内提前应用时服务器 latest 仍是旧版，清单接口只能取到 latest
        // 内容——按它修复会把刚打到新版的文件改回旧内容且无自愈。latest != staged 时必须跳过修复。
        var v1 = "v1"u8.ToArray();
        var v2 = "v2"u8.ToArray();
        await File.WriteAllBytesAsync(_tempDir.FilePath("a.dat"), v1);
        await WriteLocalState("1.0.0");

        _channel.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "1.0.0",
            PredownloadAvailable = true,
            PredownloadVersion = "2.0.0",
            PredownloadPatchSourceVersions = ["1.0.0"],
        };
        var group = BuildGroup("g1.krpdiff", [("a.dat", v1)], [("a.dat", v2)]);
        _applier.Outputs["g1.krpdiff"] = new Dictionary<string, byte[]> { ["a.dat"] = v2 };
        _channel.IncrementalManifests[("1.0.0", "2.0.0")] = new GameManifest { Version = "2.0.0", Groups = [group] };
        // 即使 latest 清单可得（守卫若失效、修复照跑也能"成功"），也不允许发出修复请求
        _channel.Manifests["2.0.0"] = new GameManifest { Version = "2.0.0", Files = [FileEntry("a.dat", v2)] };
        _downloader.Responses[Url("g1.krpdiff")] = "patch::g1.krpdiff"u8.ToArray();
        var service = CreateService();

        await service.PredownloadAsync(_tempDir.Path, _game, _server, _channel);
        var outcome = await service.ApplyPredownloadAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal("2.0.0", outcome.ToVersion);
        Assert.Equal(v2, await File.ReadAllBytesAsync(_tempDir.FilePath("a.dat")));
        Assert.Equal("2.0.0", new LocalStateService(_tempDir.Path).Load(_game.Id, _server.Id)?.Version);
        // 守卫生效的直接证据：apply 阶段没有发生任何清单拉取（修复被整体跳过）
        Assert.Empty(_channel.ManifestRequests);
    }

    [Fact]
    public async Task ApplyPredownloadAsync_ServerMatchesStagedVersion_StillRepairs()
    {
        // 守卫不得误伤常规场景：应用前服务器已切到 staged.Version（正常落地窗口），修复照常执行
        var v1 = "v1"u8.ToArray();
        var v2 = "v2"u8.ToArray();
        var b = "b-content"u8.ToArray();
        await File.WriteAllBytesAsync(_tempDir.FilePath("a.dat"), v1);
        await WriteLocalState("1.0.0");

        _channel.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "2.0.0", // 服务器已切到 2.0.0
            PredownloadAvailable = true,
            PredownloadVersion = "2.0.0",
            PredownloadPatchSourceVersions = ["1.0.0"],
        };
        var group = BuildGroup("g1.krpdiff", [("a.dat", v1)], [("a.dat", v2)]);
        _applier.Outputs["g1.krpdiff"] = new Dictionary<string, byte[]> { ["a.dat"] = v2 };
        // 增量不含 b.pak：latest == staged，事后修复必须按 2.0.0 清单补下
        _channel.IncrementalManifests[("1.0.0", "2.0.0")] = new GameManifest { Version = "2.0.0", Groups = [group] };
        _channel.Manifests["2.0.0"] = new GameManifest
        {
            Version = "2.0.0",
            Files = [FileEntry("a.dat", v2), FileEntry("b.pak", b)],
        };
        RegisterFile("b.pak", b);
        _downloader.Responses[Url("g1.krpdiff")] = "patch::g1.krpdiff"u8.ToArray();
        var service = CreateService();

        await service.PredownloadAsync(_tempDir.Path, _game, _server, _channel);
        var outcome = await service.ApplyPredownloadAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal(1, outcome.RepairedFiles);
        Assert.Contains("2.0.0", _channel.ManifestRequests);
        Assert.Equal(b, await File.ReadAllBytesAsync(_tempDir.FilePath("b.pak")));
    }

    [Fact]
    public async Task ApplyPredownloadAsync_ServerAheadOfStagedVersion_FallbackExplainsVersionDrift()
    {
        // RF-E（2026-10-02 立案）：KuroChannelApi.GetManifestAsync 忽略请求版本、恒返回服务器当前
        // 版本内容，组级回退解析缝是其唯一无守卫的消费方——服务器版本已前移时回退会拿新版内容
        // 打旧版 dst 的 md5，报误导性 "Checksum mismatch"。守卫：latest != 请求版本时折算出
        // 带版本指引的根因，而不是让用户对着校验失败排查不存在的损坏。
        var v1 = "v1"u8.ToArray();
        var v2 = "v2"u8.ToArray();
        var v3 = "v3-server-ahead"u8.ToArray();
        await WriteLocalState("2.0.0"); // 安装目录无 a.dat：组差分源缺失，触发组级回退

        _channel.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "2.0.0",
            PredownloadAvailable = true,
            PredownloadVersion = "3.0.0",
            PredownloadPatchSourceVersions = ["2.0.0"],
        };
        var group = BuildGroup("g1.krpdiff", [("a.dat", v1)], [("a.dat", v2)]);
        _channel.IncrementalManifests[("2.0.0", "3.0.0")] = new GameManifest { Version = "3.0.0", Groups = [group] };
        _channel.Manifests["3.0.0"] = new GameManifest { Version = "3.0.0", Files = [FileEntry("a.dat", v2)] };
        var service = CreateService();
        await service.PredownloadAsync(_tempDir.Path, _game, _server, _channel);

        // 应用前服务器已前移到 3.5.0。ManifestHandler 模拟被立案的渠道真实形态：
        // GetManifestAsync 对任何请求版本都返回当前版本（3.5.0）清单。
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.5.0" };
        _channel.Manifests["3.5.0"] = new GameManifest { Version = "3.5.0", Files = [FileEntry("a.dat", v3)] };
        _channel.ManifestHandler = (_, _, _) => Task.FromResult(_channel.Manifests["3.5.0"]);
        RegisterFile("a.dat", v3);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => service.ApplyPredownloadAsync(_tempDir.Path, _game, _server, _channel));

        Assert.Contains("Could not resolve a direct download", ex.Message);
        Assert.Contains("3.5.0", ex.Message);
        Assert.DoesNotContain("Checksum mismatch", ex.Message);
    }

    // ---------- 包式渠道（整包分发，如终末地） ----------

    [Fact]
    public async Task UpdateAsync_DeltaPlanAvailable_AppliesDeltaInsteadOfFullPackage()
    {
        // v2 增量优先（终末地 8.72 GiB vs 整包 58 GiB）：渠道给出计划时走增量，
        // 整包压缩包一个都不下；差分经单文件补丁器合成并落到安装目录
        await WriteLocalState("1.5.3");
        var fullContent = "delta-produced"u8.ToArray();
        var payload = TestPayload.BuildPayload(
            TestPayload.PlainEntry("GameAssembly.dll", "static"u8.ToArray()), // 静态整文件条目（覆盖游戏根）
            TestPayload.AesEntryFragment,
            TestPayload.PlainEntry(DeltaPackageApplier.ObsoleteListEntryName, Array.Empty<byte>()));
        var volumeUrl = "https://cdn.example.com/delta/payload.zip.001";
        _downloader.Responses[volumeUrl] = payload;
        var baseFile = _tempDir.FilePath("Endfield_Data", "StreamingAssets", "VFS", "base", "origin.chk");
        Directory.CreateDirectory(Path.GetDirectoryName(baseFile)!);
        File.WriteAllText(baseFile, "base-content");
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "1.6.4" };
        _channel.Manifests["1.6.4"] = new GameManifest
        {
            Version = "1.6.4",
            EntriesAreArchives = true,
            Files = [new ManifestFile("game-0.zip", 10, "aa", Url: Url("game-0.zip"))], // 整包（不应被下载）
        };
        _channel.DeltaPlan = new DeltaPackagePlan(
            FromVersion: "1.5.3",
            ToVersion: "1.6.4",
            VfsBasePath: "Endfield_Data/StreamingAssets/VFS",
            CdKey: TestPayload.AesPassword,
            Volumes: [new DeltaPackageVolume(volumeUrl, payload.Length, Md5(payload))],
            Files:
            [
                new DeltaPackageFile("AA11/bb22.chk", Hashing.Md5Hex(fullContent), fullContent.Length, 1, null,
                    [new DeltaPatchSource("base/origin.chk", Md5(File.ReadAllBytes(baseFile)), 12, "binary.bin", 0)]),
            ],
            StaticEntryExcludedPrefixes: ["vfs_files/", "diff_"],
            StaticEntryExcludedNames: [DeltaPackageApplier.ObsoleteListEntryName],
            StaticEntryRewrites: new Dictionary<string, string>(StringComparer.Ordinal));
        var filePatcher = new FakeFilePatchApplier { Output = fullContent };
        // 模拟"此前整包更新失败留下 58 GiB 暂存"
        Directory.CreateDirectory(Path.Combine(_tempDir.Path, ".yagl", "packages"));
        await File.WriteAllTextAsync(Path.Combine(_tempDir.Path, ".yagl", "packages", "stale.zip.001"), "stale");

        var outcome = await new GameUpdateService(_downloader, _applier, filePatchApplier: filePatcher)
            .UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal("1.6.4", outcome.ToVersion);
        Assert.Equal(fullContent, await File.ReadAllBytesAsync(
            _tempDir.FilePath("Endfield_Data", "StreamingAssets", "VFS", "AA11", "bb22.chk")));
        Assert.Equal("static", await File.ReadAllTextAsync(_tempDir.FilePath("GameAssembly.dll"))); // 静态条目落游戏根
        Assert.DoesNotContain(Url("game-0.zip"), _downloader.Requests); // 整包未被下载
        Assert.Single(filePatcher.Calls);
        Assert.Equal([("1.5.3", "1.6.4")], _channel.DeltaPackageRequests);
        // 上一轮整包尝试留下的数十 GiB 暂存随增量成功一并清掉（不清理就会永久占盘）
        Assert.False(Directory.Exists(Path.Combine(_tempDir.Path, ".yagl", "packages")));
    }

    [Fact]
    public async Task UpdateAsync_DeltaNotApplicable_FallsBackToFullPackageAndDropsPayload()
    {
        // 增量不适用（基文件缺失）：自动回退整包，并把白占数 GiB 的载荷暂存清掉
        await WriteLocalState("1.5.3");
        var zipBytes = TestZip.Create(("game.exe", "MZ-stub"));
        _downloader.Responses[Url("game-0.zip")] = zipBytes;
        var payload = TestPayload.BuildPayload(
            TestPayload.PlainEntry("vfs_files/files/static.dat", "static"u8.ToArray()));
        var volumeUrl = "https://cdn.example.com/delta/payload.zip.001";
        _downloader.Responses[volumeUrl] = payload;
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "1.6.4" };
        _channel.Manifests["1.6.4"] = new GameManifest
        {
            Version = "1.6.4",
            EntriesAreArchives = true,
            Files = [new ManifestFile("game-0.zip", zipBytes.Length, Md5(zipBytes), Url: Url("game-0.zip"))],
        };
        _channel.DeltaPlan = new DeltaPackagePlan(
            FromVersion: "1.5.3",
            ToVersion: "1.6.4",
            VfsBasePath: "Endfield_Data/StreamingAssets/VFS",
            CdKey: TestPayload.AesPassword,
            Volumes: [new DeltaPackageVolume(volumeUrl, payload.Length, Md5(payload))],
            Files:
            [
                new DeltaPackageFile("AA11/bb22.chk", "00", 0, 1, null,
                    [new DeltaPatchSource("gone/base.chk", "00", 0, "binary.bin", 0)]),
            ],
            StaticEntryExcludedPrefixes: ["vfs_files/"],
            StaticEntryExcludedNames: [],
            StaticEntryRewrites: new Dictionary<string, string>(StringComparer.Ordinal));
        var filePatcher = new FakeFilePatchApplier();

        var outcome = await new GameUpdateService(_downloader, _applier, filePatchApplier: filePatcher)
            .UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal("1.6.4", outcome.ToVersion);
        Assert.Equal("MZ-stub", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe"))); // 整包路径生效
        Assert.Contains(Url("game-0.zip"), _downloader.Requests);
        Assert.Empty(filePatcher.Calls);
        Assert.False(Directory.Exists(DeltaPackageApplier.PayloadDir(_tempDir.Path)));
    }

    [Fact]
    public async Task UpdateAsync_DeltaPayloadUnusable_FallsBackToFullPackage()
    {
        // review 非阻塞 4：载荷自身不可用（条目读不出）必须有整包逃生口——否则同一版本过渡内
        // 服务器一直返回同一份坏计划，用户重试恒失败（"整包永远可用"原则）
        await WriteLocalState("1.5.3");
        var zipBytes = TestZip.Create(("game.exe", "MZ-stub"));
        _downloader.Responses[Url("game-0.zip")] = zipBytes;
        var payload = TestPayload.BuildPayload(TestPayload.PlainEntry("static.dat", "static"u8.ToArray()));
        var volumeUrl = "https://cdn.example.com/delta/payload.zip.001";
        _downloader.Responses[volumeUrl] = payload;
        var baseFile = _tempDir.FilePath("Endfield_Data", "StreamingAssets", "VFS", "base", "origin.chk");
        Directory.CreateDirectory(Path.GetDirectoryName(baseFile)!);
        File.WriteAllText(baseFile, "base-content");
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "1.6.4" };
        _channel.Manifests["1.6.4"] = new GameManifest
        {
            Version = "1.6.4",
            EntriesAreArchives = true,
            Files = [new ManifestFile("game-0.zip", zipBytes.Length, Md5(zipBytes), Url: Url("game-0.zip"))],
        };
        _channel.DeltaPlan = new DeltaPackagePlan(
            FromVersion: "1.5.3",
            ToVersion: "1.6.4",
            VfsBasePath: "Endfield_Data/StreamingAssets/VFS",
            CdKey: TestPayload.AesPassword,
            Volumes: [new DeltaPackageVolume(volumeUrl, payload.Length, Md5(payload))],
            Files:
            [
                // 基文件在盘且内容相符，但载荷里没有这条补丁条目（PatchPath = binary.bin）→ 载荷不可用
                new DeltaPackageFile("AA11/bb22.chk", "00", 0, 1, null,
                    [new DeltaPatchSource("base/origin.chk", Md5("base-content"u8.ToArray()), 12, "binary.bin", 0)]),
            ],
            StaticEntryExcludedPrefixes: ["vfs_files/"],
            StaticEntryExcludedNames: [],
            StaticEntryRewrites: new Dictionary<string, string>(StringComparer.Ordinal));
        var filePatcher = new FakeFilePatchApplier();

        var outcome = await new GameUpdateService(_downloader, _applier, filePatchApplier: filePatcher)
            .UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal("1.6.4", outcome.ToVersion);
        Assert.Equal("MZ-stub", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe"))); // 整包路径生效
        Assert.Empty(filePatcher.Calls); // 载荷坏 → 未触达补丁器
        Assert.False(Directory.Exists(DeltaPackageApplier.PayloadDir(_tempDir.Path)));
    }

    [Fact]
    public async Task UpdateAsync_PackageManifest_ExtractsAndSavesState()
    {
        var zipBytes = TestZip.Create(("game.exe", "MZ-stub"));
        _downloader.Responses[Url("game-0.zip")] = zipBytes;
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "1.2.0" };
        _channel.Manifests["1.2.0"] = new GameManifest
        {
            Version = "1.2.0",
            EntriesAreArchives = true,
            Files = [new ManifestFile("game-0.zip", zipBytes.Length, Md5(zipBytes), Url: Url("game-0.zip"))],
        };

        var outcome = await CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal(UpdateStrategy.FullSync, outcome.Strategy);
        Assert.Equal("MZ-stub", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe")));
        Assert.Equal("1.2.0", new LocalStateService(_tempDir.Path).Load(_game.Id, _server.Id)?.Version);
    }

    [Fact]
    public async Task UpdateAsync_PackageChannel_DeletesOfficialObsoleteFilesAndPrunesEmptyDirectories()
    {
        // 用户实报空间占用（2026-10-17）：包式更新只覆盖不删除，旧版 VFS 分块随版本累积
        // （终末地 1.5.3→1.6.4 实测残留 47.72 GiB）。渠道给出官方废弃清单后，更新落地即删除
        var zipBytes = TestZip.Create(("game.exe", "MZ-stub"));
        _downloader.Responses[Url("game-0.zip")] = zipBytes;
        await WriteLocalState("1.0.0");
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" };
        _channel.Manifests["2.0.0"] = new GameManifest
        {
            Version = "2.0.0",
            EntriesAreArchives = true,
            Files = [new ManifestFile("game-0.zip", zipBytes.Length, Md5(zipBytes), Url: Url("game-0.zip"))],
        };
        _channel.ObsoleteFiles = ["Endfield_Data/VFS/AAAA/old.chk"];
        var stale = _tempDir.FilePath("Endfield_Data", "VFS", "AAAA", "old.chk");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        await File.WriteAllTextAsync(stale, "stale");

        var outcome = await CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal(UpdateStrategy.FullSync, outcome.Strategy);
        Assert.False(File.Exists(stale));
        Assert.False(Directory.Exists(_tempDir.FilePath("Endfield_Data"))); // 空目录随清理回收
        Assert.Equal([("1.0.0", "2.0.0")], _channel.ObsoleteFilesRequests); // 按"本地版本 → 目标版本"请求
    }

    [Fact]
    public async Task UpdateAsync_ObsoleteListUnavailable_UpdateStillSucceeds()
    {
        // 清理是尽力而为：渠道取不到清单（网络/协议漂移）不得让更新失败
        var zipBytes = TestZip.Create(("game.exe", "MZ-stub"));
        _downloader.Responses[Url("game-0.zip")] = zipBytes;
        await WriteLocalState("1.0.0");
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" };
        _channel.Manifests["2.0.0"] = new GameManifest
        {
            Version = "2.0.0",
            EntriesAreArchives = true,
            Files = [new ManifestFile("game-0.zip", zipBytes.Length, Md5(zipBytes), Url: Url("game-0.zip"))],
        };
        _channel.ObsoleteFilesError = new UpdateException("offline");

        var outcome = await CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal("2.0.0", outcome.ToVersion);
        Assert.Equal("MZ-stub", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe")));
    }

    [Fact]
    public async Task ApplyPredownloadAsync_PackageChannel_DeletesOfficialObsoleteFiles()
    {
        // 两段式（预下载 → 应用）同样清理：清单按"应用前的本地版本 → 暂存版本"请求
        var zipBytes = TestZip.Create(("config.ini", "cfg=1"));
        _downloader.Responses[Url("patch-2.0.0.zip")] = zipBytes;
        await WriteLocalState("1.0.0");
        _channel.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "1.0.0",
            PredownloadAvailable = true,
            PredownloadVersion = "2.0.0",
        };
        _channel.PredownloadManifest = new GameManifest
        {
            Version = "2.0.0",
            EntriesAreArchives = true,
            Files = [new ManifestFile("patch-2.0.0.zip", zipBytes.Length, Md5(zipBytes), Url: Url("patch-2.0.0.zip"))],
        };
        _channel.ObsoleteFiles = ["old/stale.chk"];
        var stale = _tempDir.FilePath("old", "stale.chk");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        await File.WriteAllTextAsync(stale, "stale");
        var service = CreateService();
        await service.PredownloadAsync(_tempDir.Path, _game, _server, _channel);

        await service.ApplyPredownloadAsync(_tempDir.Path, _game, _server, _channel);

        Assert.False(File.Exists(stale));
        Assert.False(Directory.Exists(_tempDir.FilePath("old")));
        Assert.Equal([("1.0.0", "2.0.0")], _channel.ObsoleteFilesRequests);
    }

    [Fact]
    public async Task PredownloadThenApply_PackageChannel_ExtractsStagedArchive()
    {
        var zipBytes = TestZip.Create(("config.ini", "cfg=1"));
        _downloader.Responses[Url("patch-2.0.0.zip")] = zipBytes;
        await WriteLocalState("1.0.0");
        _channel.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "1.0.0",
            PredownloadAvailable = true,
            PredownloadVersion = "2.0.0",
        };
        _channel.PredownloadManifest = new GameManifest
        {
            Version = "2.0.0",
            EntriesAreArchives = true,
            Files = [new ManifestFile("patch-2.0.0.zip", zipBytes.Length, Md5(zipBytes), Url: Url("patch-2.0.0.zip"))],
        };
        var service = CreateService();

        var summary = await service.PredownloadAsync(_tempDir.Path, _game, _server, _channel);
        var outcome = await service.ApplyPredownloadAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal("2.0.0", summary.ToVersion);
        Assert.Equal("cfg=1", await File.ReadAllTextAsync(_tempDir.FilePath("config.ini")));
        Assert.Equal("2.0.0", outcome.ToVersion);
        Assert.Equal("2.0.0", new LocalStateService(_tempDir.Path).Load(_game.Id, _server.Id)?.Version);
    }
}

public class GameUpdateServiceEmptyVersionTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();
    private readonly FakePatchApplier _applier = new();
    private readonly FakeChannel _channel = new();
    private readonly GameServer _server = new() { Id = "cn", Name = "国服" };

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public async Task UpdateAsync_RejectsEmptyLatestVersion_WithoutTouchingLocalState()
    {
        // 回归（2026-09-20 复审）：渠道响应版本缺失（LatestVersion=""）不得走"全量更新到空"
        // 并把空串落盘——本地版本一旦为空，IsNewer 恒判无更新，游戏永久失去更新检测
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "" };
        var service = new GameUpdateService(_downloader, _applier);
        var game = new GameDefinition
        {
            Id = "test-game",
            DisplayName = "测试游戏",
            Channel = "kuro",
            InstallDir = "TestGame",
            Executable = "game.exe",
            Servers = [_server],
        };

        await Assert.ThrowsAsync<UpdateException>(
            () => service.UpdateAsync(_tempDir.Path, game, _server, _channel));

        Assert.Null(new LocalStateService(_tempDir.Path).Load(game.Id, _server.Id));
        Assert.Empty(_downloader.Requests);
    }
}

[Collection("sequential")]
public class GameUpdateServiceArchiveIncrementGuardTests
{
    [Fact]
    public async Task UpdateAsync_IncrementalManifestContainingArchives_IsRejected()
    {
        // 回归（2026-09-20 三审，纯防御）：差分清单误带 archive 形态时曾原样交给
        // IncrementalUpdateService——压缩包会被当游戏文件搬进安装根
        using var tempDir = new TempDir();
        var channel = new FakeChannel
        {
            VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0", PatchSourceVersions = ["1.0.0"] },
        };
        channel.IncrementalManifests[("1.0.0", "2.0.0")] = new GameManifest
        {
            Version = "2.0.0",
            EntriesAreArchives = true,
            Files = [new ManifestFile("pkg.zip", 10, "aa", Url: "https://cdn.example.com/pkg.zip")],
        };
        var server = new GameServer { Id = "cn", Name = "国服" };
        var game = new GameDefinition
        {
            Id = "test-game",
            DisplayName = "测试游戏",
            Channel = "kuro",
            InstallDir = "TestGame",
            Executable = "game.exe",
            Servers = [server],
        };
        await new LocalStateService(tempDir.Path).SaveAsync(
            new LocalGameState { GameId = game.Id, ServerId = server.Id, Version = "1.0.0" });
        var downloader = new FakeDownloader();
        downloader.Responses["https://cdn.example.com/pkg.zip"] = new byte[10];

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => new GameUpdateService(downloader, new FakePatchApplier())
                .UpdateAsync(tempDir.Path, game, server, channel));

        Assert.Contains("archives", ex.Message, StringComparison.Ordinal);
        Assert.Empty(downloader.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureDownloadUrl_BlankUrl_IsRejected(string? url)
    {
        // 回归（2026-09-20 三审）：只拒 null 时空串会穿到 HttpRequestMessage 抛裸 UriFormatException
        Assert.Throws<UpdateException>(
            () => YetAnotherGameLauncher.Core.Abstractions.ManifestChecks.EnsureDownloadUrl(url!, "X", "y"));
    }
}
