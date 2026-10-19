using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>
/// 下载会话标记（2026-10-19 暂停/续传需求）：操作开始写、成功收尾清、取消/失败留存——
/// 重开应用据此提示「继续下载」（进度与产物以磁盘为准，标记不承载进度）。
/// </summary>
[Collection("sequential")]
public class DownloadSessionTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();
    private readonly GameServer _server = new() { Id = "cn", Name = "国服" };
    private readonly GameDefinition _game;

    public DownloadSessionTests()
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

    private static string Url(string name) => $"https://cdn.example.com/{name}";

    private static ManifestFile FileEntry(string path, byte[] content) =>
        new(path, content.Length, Hashing.Md5Hex(content), Url: Url(path.Replace('/', '_')));

    private void RegisterFile(string path, byte[] content) =>
        _downloader.Responses[Url(path.Replace('/', '_'))] = content;

    [Fact]
    public void Begin_ThenLoad_RoundTrips()
    {
        var service = new DownloadSessionService(_tempDir.Path);

        service.Begin("update", "3.7.1");

        var session = service.Load();
        Assert.NotNull(session);
        Assert.Equal("update", session!.Kind);
        Assert.Equal("3.7.1", session.Version);
        Assert.True(session.StartedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Load_NoFile_ReturnsNull() => Assert.Null(new DownloadSessionService(_tempDir.Path).Load());

    [Fact]
    public void LoadFor_OtherServerMarker_IsIgnored()
    {
        // 同一安装目录被多服务器复用（切服只换状态上下文）：标记只对写入它的游戏/服务器有效（2026-10-19 review）
        var service = new DownloadSessionService(_tempDir.Path);
        service.Begin("update", "3.7.1", "wuthering-waves", "cn");

        Assert.NotNull(service.LoadFor("wuthering-waves", "cn"));
        Assert.Null(service.LoadFor("wuthering-waves", "global"));
        Assert.Null(service.LoadFor("arknights-endfield", "cn"));
    }

    [Fact]
    public void LoadFor_LegacyMarkerWithoutOwner_MatchesAnyServer()
    {
        // 旧版本标记没有归属字段：按"任意归属"处理，升级不丢续传提示
        var service = new DownloadSessionService(_tempDir.Path);
        service.Begin("update", "3.7.1");

        Assert.NotNull(service.LoadFor("wuthering-waves", "global"));
    }

    [Fact]
    public void Begin_RecordsKindAndVersionForResumeDispatch()
    {
        var service = new DownloadSessionService(_tempDir.Path);
        service.Begin(DownloadSessionKind.Predownload, "3.7.2", "wuthering-waves", "cn");

        var session = service.LoadFor("wuthering-waves", "cn");

        Assert.NotNull(session);
        Assert.Equal(DownloadSessionKind.Predownload, session!.Kind);
        Assert.Equal("3.7.2", session.Version);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        var service = new DownloadSessionService(_tempDir.Path);
        service.Begin("update");
        File.WriteAllText(Path.Combine(_tempDir.Path, ".yagl", DownloadSessionService.FileName), "{not json");

        Assert.Null(service.Load());
    }

    [Fact]
    public void Clear_RemovesMarker()
    {
        var service = new DownloadSessionService(_tempDir.Path);
        service.Begin("install");

        service.Clear();

        Assert.Null(service.Load());
    }

    [Fact]
    public async Task UpdateAsync_Success_ClearsMarker()
    {
        var a = "aaa"u8.ToArray();
        RegisterFile("a.dat", a);
        var channel = new FakeChannel
        {
            VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" },
        };
        channel.Manifests["2.0.0"] = new GameManifest { Version = "2.0.0", Files = [FileEntry("a.dat", a)] };

        await new GameUpdateService(_downloader, new FakePatchApplier())
            .UpdateAsync(_tempDir.Path, _game, _server, channel);

        Assert.Null(new DownloadSessionService(_tempDir.Path).Load());
    }

    [Fact]
    public async Task UpdateAsync_Failure_KeepsMarkerForResume()
    {
        // 更新失败（清单拉不到）：标记留存——重开应用提示「继续下载」，重跑从已下载处接上
        var channel = new FakeChannel
        {
            VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" },
            ManifestError = new UpdateException("boom"),
        };

        await Assert.ThrowsAsync<UpdateException>(() =>
            new GameUpdateService(_downloader, new FakePatchApplier())
                .UpdateAsync(_tempDir.Path, _game, _server, channel));

        Assert.NotNull(new DownloadSessionService(_tempDir.Path).Load());
    }

    [Fact]
    public async Task UpdateAsync_Cancelled_KeepsMarkerForResume()
    {
        // 取消（含"暂停"）：产物保留、标记留存——续传语义的关键前提
        using var cts = new CancellationTokenSource();
        var channel = new FakeChannel
        {
            VersionInfo = new ChannelVersionInfo { LatestVersion = "2.0.0" },
        };
        var entry = FileEntry("a.dat", "aaa"u8.ToArray());
        channel.Manifests["2.0.0"] = new GameManifest { Version = "2.0.0", Files = [entry] };
        channel.ManifestHandler = (_, _, _, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(channel.Manifests["2.0.0"]);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GameUpdateService(_downloader, new FakePatchApplier())
                .UpdateAsync(_tempDir.Path, _game, _server, channel, cancellationToken: cts.Token));

        Assert.NotNull(new DownloadSessionService(_tempDir.Path).Load());
    }

    [Fact]
    public async Task PredownloadAsync_Success_ClearsMarker()
    {
        var delta = "delta"u8.ToArray();
        await new LocalStateService(_tempDir.Path).SaveAsync(
            new LocalGameState { GameId = _game.Id, ServerId = _server.Id, Version = "1.0.0" });
        var channel = new FakeChannel
        {
            VersionInfo = new ChannelVersionInfo
            {
                LatestVersion = "1.0.0",
                PredownloadAvailable = true,
                PredownloadVersion = "2.0.0",
                PredownloadPatchSourceVersions = ["1.0.0"],
            },
        };
        channel.IncrementalManifests[("1.0.0", "2.0.0")] = new GameManifest
        {
            Version = "2.0.0",
            Files = [FileEntry("b.dat", delta)],
        };
        RegisterFile("b.dat", delta);

        await new GameUpdateService(_downloader, new FakePatchApplier())
            .PredownloadAsync(_tempDir.Path, _game, _server, channel);

        Assert.Null(new DownloadSessionService(_tempDir.Path).Load());
    }
}
