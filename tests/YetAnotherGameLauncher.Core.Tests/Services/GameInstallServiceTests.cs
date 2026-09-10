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
    public async Task SyncAsync_SameSizeCorruption_RepairedOnSecondPass()
    {
        // 快速校验只比存在性与大小：同尺寸但内容损坏的文件由 MD5 事后校验兜底补下载修复
        var a = "content-a"u8.ToArray();
        var corrupt = "corrupt-X"u8.ToArray(); // 与 a 同为 9 字节
        Assert.Equal(a.Length, corrupt.Length);
        await File.WriteAllBytesAsync(_tempDir.FilePath("a.txt"), corrupt);
        _downloader.Responses[Url("a.txt")] = a;
        var service = new GameInstallService(_downloader);

        var repaired = await service.SyncAsync(_tempDir.Path, Manifest(FileEntry("a.txt", a)));

        Assert.Equal(1, repaired);
        Assert.Equal([Url("a.txt")], _downloader.Requests);
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

        Assert.Contains("Post-download verification failed", ex.Message);
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
        var reports = new System.Collections.Concurrent.ConcurrentQueue<UpdateProgress>();
        var progress = new Progress<UpdateProgress>(reports.Enqueue);

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

    [Fact]
    public async Task SyncAsync_NeverDeletesWinePrefixCompatdata()
    {
        // 旧版推荐配置把 Proton prefix 放在 {installDir}/compatdata：它不在清单内，
        // 但清理绝不能碰（里面有注册表/着色器缓存/用户数据，删了等于毁掉游戏环境）
        var a = "content-a"u8.ToArray();
        _downloader.Responses[Url("a.txt")] = a;
        var prefixFile = _tempDir.FilePath("compatdata", "pfx", "drive_c", "users", "steamuser");
        Directory.CreateDirectory(prefixFile);
        await File.WriteAllTextAsync(Path.Combine(prefixFile, "user.reg"), "[REG]");

        var service = new GameInstallService(_downloader);
        await service.SyncAsync(_tempDir.Path, Manifest(FileEntry("a.txt", a)));

        Assert.True(File.Exists(Path.Combine(prefixFile, "user.reg")));
    }

    [Fact]
    public async Task SyncAsync_SingleDeletionFailure_DoesNotAbortSync()
    {
        // Linux：把含游离文件的目录改为不可写，单个删除失败只跳过该文件，不中断整轮同步
        var a = "content-a"u8.ToArray();
        _downloader.Responses[Url("a.txt")] = a;
        var lockedDir = _tempDir.FilePath("locked-stale");
        Directory.CreateDirectory(lockedDir);
        await File.WriteAllTextAsync(Path.Combine(lockedDir, "stray.bin"), "x");
        await File.WriteAllTextAsync(_tempDir.FilePath("plain-stray.log"), "y");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        try
        {
            var service = new GameInstallService(_downloader);
            await service.SyncAsync(_tempDir.Path, Manifest(FileEntry("a.txt", a)));

            Assert.True(File.Exists(_tempDir.FilePath("a.txt")));
            Assert.False(File.Exists(_tempDir.FilePath("plain-stray.log"))); // 其它游离文件照常清理
            if (OperatingSystem.IsWindows())
            {
                Assert.False(File.Exists(Path.Combine(lockedDir, "stray.bin")));
            }
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }
}
