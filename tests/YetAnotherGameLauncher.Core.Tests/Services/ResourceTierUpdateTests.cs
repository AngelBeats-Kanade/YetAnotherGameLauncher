using Xunit;

using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>
/// 档位下载的 Core 编排（2026-10-19）：请求档位随更新链传下去、落盘状态记"这次到底落了哪档"、
/// 清理游离文件尊重清单的保留前缀（未选中档位目录不得被隐式删除）。
/// </summary>
[Collection("sequential")]
public class ResourceTierUpdateTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();
    private readonly FakePatchApplier _applier = new();
    private readonly FakeChannel _channel = new();
    private readonly GameServer _server = new() { Id = "cn", Name = "国服" };
    private readonly GameDefinition _game;

    public ResourceTierUpdateTests()
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
        new(path, content.Length, Md5(content), Url(path.Replace('/', '_')));

    private GameUpdateService CreateService() => new(_downloader, _applier);

    private void RegisterFile(string path, byte[] content) =>
        _downloader.Responses[Url(path.Replace('/', '_'))] = content;

    private LocalGameState? LoadState() => new LocalStateService(_tempDir.Path).Load(_game.Id, _server.Id);

    // ---------- 全量路径：请求档位传下去、落盘档位取清单戳记 ----------

    [Fact]
    public async Task UpdateAsync_TieredChannel_PassesConfiguredTierAndRecordsManifestTier()
    {
        var a = "aaa"u8.ToArray();
        RegisterFile("a.dat", a);
        _channel.SupportsResourcePacks = true;
        _game.Launch.ResourceQualityTier = "uhd";
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" };
        _channel.Manifests["2.0.0"] = new GameManifest { Version = "2.0.0", Files = [FileEntry("a.dat", a)], ResourceTier = "uhd" };

        await CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal(["uhd"], _channel.ManifestTiers);
        Assert.Equal("uhd", LoadState()!.ResourceTier);
    }

    [Fact]
    public async Task UpdateAsync_UntieredChannel_PassesNoTierAndRecordsManifestStamp()
    {
        // 不区分档位的渠道（终末地）与不支持档位下载的服务器（Kuro 国际服）：Core 不传档位，
        // 落盘档位只认清单戳记（渠道归一规则的单一事实源在渠道）
        var a = "aaa"u8.ToArray();
        RegisterFile("a.dat", a);
        _channel.SupportsResourcePacks = false;
        _game.Launch.ResourceQualityTier = "uhd";
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" };
        _channel.Manifests["2.0.0"] = new GameManifest { Version = "2.0.0", Files = [FileEntry("a.dat", a)] };

        await CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal([null], _channel.ManifestTiers);
        Assert.Null(LoadState()!.ResourceTier);
    }

    [Fact]
    public async Task UpdateAsync_IncrementalTiered_PassesTierToManifestAndRepair()
    {
        var oldContent = "old"u8.ToArray();
        var newContent = "new"u8.ToArray();
        _channel.SupportsResourcePacks = true;
        _game.Launch.ResourceQualityTier = "sd";
        await new LocalStateService(_tempDir.Path).SaveAsync(
            new LocalGameState { GameId = _game.Id, ServerId = _server.Id, Version = "1.0.0" });
        File.WriteAllBytes(_tempDir.FilePath("a.dat"), oldContent);

        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0", PatchSourceVersions = ["1.0.0"] };
        _channel.IncrementalManifests[("1.0.0", "2.0.0")] = new GameManifest
        {
            Version = "2.0.0",
            Files = [FileEntry("Client/Content/SD/pack.pak", newContent)],
            ResourceTier = "sd",
        };
        RegisterFile("Client/Content/SD/pack.pak", newContent);
        // 修复轮的全量清单（版本一致）：本次已经是最新，修复轮只做校验
        _channel.Manifests["2.0.0"] = new GameManifest
        {
            Version = "2.0.0",
            Files = [FileEntry("Client/Content/SD/pack.pak", newContent)],
            ResourceTier = "sd",
            PreservedPaths = ["Client/Content/HD", "Client/Content/UHD"],
        };

        var outcome = await CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal(UpdateStrategy.Incremental, outcome.Strategy);
        Assert.Equal(["sd"], _channel.IncrementalManifestTiers);
        Assert.Equal(["sd"], _channel.ManifestTiers);
        Assert.Equal("sd", LoadState()!.ResourceTier);
    }

    // ---------- 预下载：档位随暂存清单落盘（供应用轮记录"真正落盘的档"）----------

    [Fact]
    public async Task PredownloadAsync_TieredChannel_StagesManifestWithTierStamp()
    {
        var delta = "delta"u8.ToArray();
        _channel.SupportsResourcePacks = true;
        _game.Launch.ResourceQualityTier = "uhd";
        await new LocalStateService(_tempDir.Path).SaveAsync(
            new LocalGameState { GameId = _game.Id, ServerId = _server.Id, Version = "1.0.0" });
        _channel.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "1.0.0",
            PredownloadAvailable = true,
            PredownloadVersion = "2.0.0",
            PredownloadPatchSourceVersions = ["1.0.0"],
        };
        _channel.IncrementalManifests[("1.0.0", "2.0.0")] = new GameManifest
        {
            Version = "2.0.0",
            Files = [FileEntry("b.dat", delta)],
            ResourceTier = "uhd",
        };
        RegisterFile("b.dat", delta);

        await CreateService().PredownloadAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal(["uhd"], _channel.IncrementalManifestTiers);
        var staged = IncrementalUpdateService.TryLoadStagedManifest(_tempDir.Path);
        Assert.NotNull(staged);
        Assert.Equal("uhd", staged.ResourceTier);
    }

    [Fact]
    public async Task ApplyPredownload_RepairSkipped_RecordsStagedTierNotConfiguredTier()
    {
        // 窗口期提前应用（服务器 latest 与暂存版本不一致）→ 跳过修复：落盘的是暂存内容
        // （hd 档），不得把状态记成用户已选的 uhd——那会让 UI 误报"档位已就绪"
        var stagedContent = "hd-pack"u8.ToArray();
        _channel.SupportsResourcePacks = true;
        _game.Launch.ResourceQualityTier = "uhd";
        var staging = IncrementalUpdateService.PredownloadDir(_tempDir.Path);
        Directory.CreateDirectory(staging);
        await IncrementalUpdateService.WriteStagedManifestAsync(
            staging,
            new GameManifest
            {
                Version = "2.0.0",
                Files = [new ManifestFile("Client/Content/HD/pack.pak", stagedContent.Length, Md5(stagedContent))],
                ResourceTier = ResourceTiers.Hd,
            },
            CancellationToken.None);
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.0.0" }; // 服务器已前移

        var outcome = await CreateService().ApplyPredownloadAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal("2.0.0", outcome.ToVersion);
        Assert.Equal("hd", LoadState()!.ResourceTier);
    }

    [Fact]
    public async Task ApplyPredownload_RepairRuns_RecordsTierVerifiedByRepairManifest()
    {
        // 版本一致的常规应用：修复轮按用户当前档位（sd）取全量清单核实 → 落盘档位记 sd
        var stagedContent = "hd-pack"u8.ToArray();
        var sdPack = "sd-pack"u8.ToArray();
        _channel.SupportsResourcePacks = true;
        _game.Launch.ResourceQualityTier = "sd";
        var staging = IncrementalUpdateService.PredownloadDir(_tempDir.Path);
        Directory.CreateDirectory(staging);
        await IncrementalUpdateService.WriteStagedManifestAsync(
            staging,
            new GameManifest
            {
                Version = "2.0.0",
                Files = [new ManifestFile("Client/Content/HD/pack.pak", stagedContent.Length, Md5(stagedContent))],
                ResourceTier = ResourceTiers.Hd,
            },
            CancellationToken.None);
        Directory.CreateDirectory(_tempDir.FilePath("Client/Content/SD"));
        File.WriteAllBytes(_tempDir.FilePath("Client/Content/SD/pack.pak"), sdPack);
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" }; _channel.Manifests["2.0.0"] = new GameManifest
        {
            Version = "2.0.0",
            Files = [FileEntry("Client/Content/SD/pack.pak", sdPack)],
            ResourceTier = ResourceTiers.Sd,
            PreservedPaths = ["Client/Content/HD", "Client/Content/UHD"],
        };

        await CreateService().ApplyPredownloadAsync(_tempDir.Path, _game, _server, _channel);

        Assert.Equal(["sd"], _channel.ManifestTiers);
        Assert.Equal("sd", LoadState()!.ResourceTier);
    }

    // ---------- 清理游离文件：保留前缀（未选中档位目录）不得被隐式删除 ----------

    [Fact]
    public async Task FullSync_PreservedTierDirectory_NotDeletedAsStale()
    {
        var a = "aaa"u8.ToArray();
        RegisterFile("a.dat", a);
        Directory.CreateDirectory(_tempDir.FilePath("Client/Content/UHD"));
        File.WriteAllBytes(_tempDir.FilePath("Client/Content/UHD/pack.pak"), "uhd-pack"u8.ToArray());
        File.WriteAllBytes(_tempDir.FilePath("stale.txt"), "stale"u8.ToArray());
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" };
        _channel.Manifests["2.0.0"] = new GameManifest
        {
            Version = "2.0.0",
            Files = [FileEntry("a.dat", a)],
            ResourceTier = ResourceTiers.Sd,
            PreservedPaths = ["/Client/Content/UHD"],
        };

        await CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.True(File.Exists(_tempDir.FilePath("Client/Content/UHD/pack.pak")), "未选中档位目录被当游离文件删掉了");
        Assert.False(File.Exists(_tempDir.FilePath("stale.txt")), "清单外的普通游离文件应照常清理");
    }

    [Fact]
    public async Task FullSync_PreservedPrefixIsPathSegmentWise_NotStringPrefix()
    {
        // 前缀匹配以目录段为界：Client/Content/HD 的保留不得连带放过 Client/Content/HDX（同段名族陷阱）
        var a = "aaa"u8.ToArray();
        RegisterFile("a.dat", a);
        Directory.CreateDirectory(_tempDir.FilePath("Client/Content/HDX"));
        File.WriteAllBytes(_tempDir.FilePath("Client/Content/HDX/other.pak"), "other"u8.ToArray());
        Directory.CreateDirectory(_tempDir.FilePath("Client/Content/HD"));
        File.WriteAllBytes(_tempDir.FilePath("Client/Content/HD/pack.pak"), "hd-pack"u8.ToArray());
        _channel.VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" };
        _channel.Manifests["2.0.0"] = new GameManifest
        {
            Version = "2.0.0",
            Files = [FileEntry("a.dat", a)],
            ResourceTier = ResourceTiers.Uhd,
            PreservedPaths = ["Client/Content/HD"],
        };

        await CreateService().UpdateAsync(_tempDir.Path, _game, _server, _channel);

        Assert.True(File.Exists(_tempDir.FilePath("Client/Content/HD/pack.pak")));
        Assert.False(File.Exists(_tempDir.FilePath("Client/Content/HDX/other.pak")));
    }
}
