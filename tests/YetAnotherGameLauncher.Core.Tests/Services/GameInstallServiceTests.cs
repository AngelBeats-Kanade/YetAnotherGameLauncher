using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.Core.Utilities;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class GameInstallServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();

    public void Dispose() => _tempDir.Dispose();

    private static string Md5(byte[] data) => Hashing.Md5Hex(data);

    private static string Url(string path) => $"https://cdn.example.com/{path}";

    private static ManifestFile FileEntry(string path, byte[] content) =>
        new(path, content.Length, Md5(content), Url: Url(path));

    private GameManifest Manifest(params ManifestFile[] files) => new()
    {
        Version = "1.0.0",
        Files = files,
    };

    [Fact]
    public async Task SyncAsync_DownloadsAllMissingFiles()
    {
        var a = "content-a"u8.ToArray();
        var b = "content-b!!"u8.ToArray();
        _downloader.Responses[Url("a.txt")] = a;
        _downloader.Responses[Url("sub/b.txt")] = b;
        var service = new GameInstallService(_downloader);

        await service.SyncAsync(_tempDir.Path, Manifest(FileEntry("a.txt", a), FileEntry("sub/b.txt", b)));

        Assert.Equal(a, await File.ReadAllBytesAsync(_tempDir.FilePath("a.txt")));
        Assert.Equal(b, await File.ReadAllBytesAsync(_tempDir.FilePath("sub", "b.txt")));
    }

    [Fact]
    public async Task SyncAsync_SkipsAlreadyValidFiles()
    {
        var a = "content-a"u8.ToArray();
        var b = "content-b!!"u8.ToArray();
        await File.WriteAllBytesAsync(_tempDir.FilePath("a.txt"), a);
        _downloader.Responses[Url("b.txt")] = b;
        var service = new GameInstallService(_downloader);

        await service.SyncAsync(_tempDir.Path, Manifest(FileEntry("a.txt", a), FileEntry("b.txt", b)));

        // a.txt 已有效：不应出现在下载请求中
        Assert.Equal([Url("b.txt")], _downloader.Requests);
    }

    [Fact]
    public async Task SyncAsync_ReplacesCorruptedFile()
    {
        var a = "content-a"u8.ToArray();
        await File.WriteAllBytesAsync(_tempDir.FilePath("a.txt"), "short"u8.ToArray());
        _downloader.Responses[Url("a.txt")] = a;
        var service = new GameInstallService(_downloader);

        await service.SyncAsync(_tempDir.Path, Manifest(FileEntry("a.txt", a)));

        Assert.Equal(a, await File.ReadAllBytesAsync(_tempDir.FilePath("a.txt")));
    }

    [Fact]
    public async Task SyncAsync_PostVerificationFailure_Throws()
    {
        // 下载器写入的内容与清单 MD5 不符 → 事后校验必须失败
        var a = "content-a"u8.ToArray();
        _downloader.Responses[Url("a.txt")] = "tampered"u8.ToArray();
        var service = new GameInstallService(_downloader);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => service.SyncAsync(_tempDir.Path, Manifest(FileEntry("a.txt", a))));

        Assert.Contains("完整性校验失败", ex.Message);
    }

    [Fact]
    public async Task SyncAsync_MissingUrl_Throws()
    {
        var a = "content-a"u8.ToArray();
        var service = new GameInstallService(_downloader);
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [new ManifestFile("a.txt", a.Length, Md5(a))], // 无 Url
        };

        await Assert.ThrowsAsync<UpdateException>(() => service.SyncAsync(_tempDir.Path, manifest));
    }

    [Fact]
    public async Task SyncAsync_ReportsDoneProgress()
    {
        var a = "content-a"u8.ToArray();
        var b = "content-b!!"u8.ToArray();
        _downloader.Responses[Url("a.txt")] = a;
        _downloader.Responses[Url("b.txt")] = b;
        var service = new GameInstallService(_downloader);
        var reports = new List<UpdateProgress>();
        var progress = new Progress<UpdateProgress>(reports.Add);

        await service.SyncAsync(_tempDir.Path, Manifest(FileEntry("a.txt", a), FileEntry("b.txt", b)), progress);

        // Progress<T> 回调为异步投递，等待最终的 Done 报告送达（最多 5 秒）
        Assert.True(SpinWait.SpinUntil(
            () => reports.Any(r => r.Phase == UpdatePhase.Done), TimeSpan.FromSeconds(5)));
        var done = reports.Last(r => r.Phase == UpdatePhase.Done);
        Assert.Equal(UpdatePhase.Done, done.Phase);
        Assert.Equal(2, done.FilesTotal);
        Assert.Equal(2, done.FilesDone);
        Assert.Equal(a.Length + b.Length, done.TotalBytes);
    }

    [Fact]
    public async Task SyncAsync_CleansStaleFilesButPreservesSavedAndYagl()
    {
        var a = "content-a"u8.ToArray();
        _downloader.Responses[Url("a.txt")] = a;
        Directory.CreateDirectory(_tempDir.FilePath("Saved"));
        Directory.CreateDirectory(_tempDir.FilePath(".yagl"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("Saved", "save.bin"), [1]);
        await File.WriteAllBytesAsync(_tempDir.FilePath(".yagl", "state.json"), [2]);
        await File.WriteAllBytesAsync(_tempDir.FilePath("launcherDownloadConfig.json"), [3]);
        await File.WriteAllBytesAsync(_tempDir.FilePath("stray-old.log"), [4]);
        var service = new GameInstallService(_downloader);

        await service.SyncAsync(_tempDir.Path, Manifest(FileEntry("a.txt", a)));

        Assert.True(File.Exists(_tempDir.FilePath("Saved", "save.bin")));
        Assert.True(File.Exists(_tempDir.FilePath(".yagl", "state.json")));
        Assert.True(File.Exists(_tempDir.FilePath("launcherDownloadConfig.json")));
        Assert.False(File.Exists(_tempDir.FilePath("stray-old.log")));
        Assert.True(File.Exists(_tempDir.FilePath("a.txt")));
    }
}
