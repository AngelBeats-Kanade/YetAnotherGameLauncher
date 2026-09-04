using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.Core.Utilities;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>假渠道：可配置版本信息与清单。</summary>
internal sealed class FakeChannel : IGameChannelApi
{
    public ChannelVersionInfo VersionInfo { get; set; } = new() { LatestVersion = "2.0.0" };

    public Dictionary<string, GameManifest> Manifests { get; } = new(StringComparer.Ordinal);

    public Dictionary<(string From, string To), GameManifest> IncrementalManifests { get; } = new();

    public GameManifest? PredownloadManifest { get; set; }

    public List<string> ManifestRequests { get; } = [];

    public Task<ChannelVersionInfo> GetVersionInfoAsync(GameServer server, CancellationToken cancellationToken = default) =>
        Task.FromResult(VersionInfo);

    public Task<GameManifest> GetManifestAsync(GameServer server, string version, CancellationToken cancellationToken = default)
    {
        ManifestRequests.Add(version);
        return Task.FromResult(Manifests[version]);
    }

    public Task<GameManifest?> GetPredownloadManifestAsync(GameServer server, CancellationToken cancellationToken = default) =>
        Task.FromResult(PredownloadManifest);

    public Task<GameManifest?> GetIncrementalManifestAsync(
        GameServer server, string fromVersion, string toVersion, CancellationToken cancellationToken = default) =>
        Task.FromResult<GameManifest?>(IncrementalManifests.TryGetValue((fromVersion, toVersion), out var manifest)
            ? manifest
            : null);
}

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

    // ---------- 预下载（两段式） ----------

    [Fact]
    public async Task PredownloadAsync_NoWindow_Throws()
    {
        await WriteLocalState("1.0.0");
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "1.0.0", PredownloadAvailable = false };

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().PredownloadAsync(_tempDir.Path, _game, _server, _channel));

        Assert.Contains("预下载", ex.Message);
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

        Assert.Contains("没有适用于本地版本", ex.Message);
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

    // ---------- 包式渠道（整包分发，如终末地） ----------

    [Fact]
    public async Task UpdateAsync_PackageManifest_ExtractsAndSavesState()
    {
        var zipBytes = Core.Tests.TestZip.Create(("game.exe", "MZ-stub"));
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
    public async Task PredownloadThenApply_PackageChannel_ExtractsStagedArchive()
    {
        var zipBytes = Core.Tests.TestZip.Create(("config.ini", "cfg=1"));
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
