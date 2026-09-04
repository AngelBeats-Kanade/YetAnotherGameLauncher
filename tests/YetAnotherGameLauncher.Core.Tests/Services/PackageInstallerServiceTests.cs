using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.Core.Utilities;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class PackageInstallerServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();

    public void Dispose() => _tempDir.Dispose();

    private const string ZipUrl = "https://cdn.example.com/game-0.zip";

    private GameManifest Manifest() => new()
    {
        Version = "1.2.0",
        EntriesAreArchives = true,
        Files =
        [
            new ManifestFile("game-0.zip", ZipBytes.Length, Hashing.Md5Hex(ZipBytes), Url: ZipUrl),
        ],
    };

    private static readonly byte[] ZipBytes = TestZip.Create(("game.exe", "MZ-stub"), ("config.ini", "cfg=1"));

    [Fact]
    public async Task InstallAsync_DownloadsAndExtractsArchive()
    {
        _downloader.Responses[ZipUrl] = ZipBytes;

        await new PackageInstallerService(_downloader).InstallAsync(_tempDir.Path, Manifest());

        Assert.Equal("MZ-stub", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe")));
        Assert.Equal("cfg=1", await File.ReadAllTextAsync(_tempDir.FilePath("config.ini")));
        // 临时压缩包目录被清理
        Assert.False(Directory.Exists(Path.Combine(_tempDir.Path, ".yagl", "packages")));
    }

    [Fact]
    public async Task Predownload_StagesArchiveWithoutExtracting()
    {
        _downloader.Responses[ZipUrl] = ZipBytes;

        await new PackageInstallerService(_downloader).PredownloadAsync(_tempDir.Path, Manifest());

        Assert.True(File.Exists(Path.Combine(
            IncrementalUpdateService.PredownloadDir(_tempDir.Path), "packages", "game-0.zip")));
        Assert.False(File.Exists(_tempDir.FilePath("game.exe")));
        // 暂存清单已写入，供 apply 阶段读取
        Assert.NotNull(IncrementalUpdateService.TryLoadStagedManifest(_tempDir.Path));
    }

    [Fact]
    public async Task PredownloadThenApply_ExtractsIntoInstallDir()
    {
        _downloader.Responses[ZipUrl] = ZipBytes;
        var service = new PackageInstallerService(_downloader);

        await service.PredownloadAsync(_tempDir.Path, Manifest());
        await service.ApplyPredownloadAsync(_tempDir.Path, Manifest());

        Assert.Equal("MZ-stub", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe")));
        Assert.False(Directory.Exists(IncrementalUpdateService.PredownloadDir(_tempDir.Path)));
    }

    [Fact]
    public async Task InstallAsync_Md5Mismatch_Throws()
    {
        // 用真实下载器 + 桩 HTTP 验证 md5 链路
        var handler = new StubHttpHandler();
        handler.Map(ZipUrl, new byte[] { 0xDE, 0xAD });
        var service = new PackageInstallerService(
            new HttpFileDownloader(new HttpClient(handler), new HttpFileDownloaderOptions { MaxAttempts = 1 }));

        await Assert.ThrowsAsync<DownloadVerificationException>(
            () => service.InstallAsync(_tempDir.Path, Manifest()));
    }
}
