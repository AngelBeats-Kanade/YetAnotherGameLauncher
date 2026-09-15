using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class PackageInstallerServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();

    public void Dispose() => _tempDir.Dispose();

    private const string ZipUrl = "https://cdn.example.com/game-0.zip";

    private GameManifest Manifest() => ManifestFor(ZipBytes);

    private static GameManifest ManifestFor(byte[] zip) => new()
    {
        Version = "1.2.0",
        EntriesAreArchives = true,
        Files =
        [
            new ManifestFile("game-0.zip", zip.Length, Hashing.Md5Hex(zip), Url: ZipUrl),
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
    public async Task ApplyPredownload_UsesStagedArchive_WithoutReDownloading()
    {
        // 回归：Apply 曾误走 InstallAsync，把预下载暂存的包弃用后整包重新下载
        _downloader.Responses[ZipUrl] = ZipBytes;
        var service = new PackageInstallerService(_downloader);
        await service.PredownloadAsync(_tempDir.Path, Manifest());
        var requestsAfterPredownload = _downloader.Requests.Count;

        await service.ApplyPredownloadAsync(_tempDir.Path, Manifest());

        Assert.Equal(requestsAfterPredownload, _downloader.Requests.Count); // 应用阶段零下载
        Assert.False(File.Exists(_tempDir.FilePath("game-0.zip"))); // 暂存包已随暂存目录清理
    }

    [Fact]
    public async Task ApplyPredownload_CorruptStagedArchive_FallsBackToReDownload()
    {
        _downloader.Responses[ZipUrl] = ZipBytes;
        var service = new PackageInstallerService(_downloader);
        await service.PredownloadAsync(_tempDir.Path, Manifest());
        // 篡改暂存包使其校验失败（截断）
        var staged = Path.Combine(
            IncrementalUpdateService.PredownloadDir(_tempDir.Path), "packages", "game-0.zip");
        await File.WriteAllBytesAsync(staged, ZipBytes[..^8]);

        await service.ApplyPredownloadAsync(_tempDir.Path, Manifest());

        Assert.True(_downloader.Requests.Count > 1); // 该包被重新下载
        Assert.Equal("MZ-stub", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe")));
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

    [Fact]
    public async Task InstallAsync_BackslashEntryNames_ExtractIntoNestedDirectories()
    {
        // dotnet/runtime#98247：ExtractToDirectory 在 Unix 把 "Client\Foo.pak" 当字面文件名，
        // Windows 打包器产出的包会在 Linux 解成安装根目录下的平铺垃圾文件；
        // 手动解压必须把条目名归一成 '/'（修复前本用例在 Linux 必红）
        var zip = TestZip.Create(("Client\\Saved\\file.pak", "pak"), ("readme.txt", "hi"));
        _downloader.Responses[ZipUrl] = zip;

        await new PackageInstallerService(_downloader).InstallAsync(_tempDir.Path, ManifestFor(zip));

        Assert.Equal("pak", await File.ReadAllTextAsync(_tempDir.FilePath("Client", "Saved", "file.pak")));
        Assert.Equal("hi", await File.ReadAllTextAsync(_tempDir.FilePath("readme.txt")));
    }

    [Fact]
    public async Task InstallAsync_EntryEscapingSandbox_ThrowsAndWritesNothing()
    {
        // 条目名含 .. 拒绝解压（清单/包均不可信，防穿越）
        var zip = TestZip.Create(("../evil.txt", "evil"));
        _downloader.Responses[ZipUrl] = zip;

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => new PackageInstallerService(_downloader).InstallAsync(_tempDir.Path, ManifestFor(zip)));

        Assert.Contains("escapes", ex.Message);
        Assert.False(File.Exists(Path.Combine(_tempDir.Path, "..", "evil.txt")));
        Assert.False(File.Exists(_tempDir.FilePath("evil.txt")));
    }

    [Fact]
    public async Task InstallAsync_ExistingReadOnlyTarget_Overwritten()
    {
        // Windows 上只读目标会让 overwrite 抛 UnauthorizedAccessException；解压前就地解除属性
        var target = _tempDir.FilePath("game.exe");
        await File.WriteAllTextAsync(target, "old");
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);

        var zip = TestZip.Create(("game.exe", "new"));
        _downloader.Responses[ZipUrl] = zip;

        await new PackageInstallerService(_downloader).InstallAsync(_tempDir.Path, ManifestFor(zip));

        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
    }
}
