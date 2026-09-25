using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

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
    public async Task SyncAsync_Cleanup_DoesNotFollowDirectoryLinks()
    {
        // 用户把安装目录内的子目录搬到安装树之外再用目录链接接回（Windows 玩家常见搬盘手法）：
        // 清理游离文件绝不穿过链接——链接目标处（安装目录之外）的真实文件必须完好，链接本身也不拆。
        // 词法相对路径会把"链接内可见的清单外文件"判成游离并删穿链接，即此回归。
        var outside = _tempDir.FilePath("outside");
        Directory.CreateDirectory(outside);
        var userFile = Path.Combine(outside, "userfile.txt");
        await File.WriteAllTextAsync(userFile, "keep");

        var installDir = _tempDir.FilePath("install");
        Directory.CreateDirectory(installDir);
        var link = Path.Combine(installDir, "linked");
        CreateDirectoryLink(link, outside);
        await File.WriteAllTextAsync(Path.Combine(link, "stale.txt"), "seen-through-link");

        var a = "content-a"u8.ToArray();
        _downloader.Responses[Url("a.txt")] = a;
        var service = new GameInstallService(_downloader);

        await service.SyncAsync(installDir, Manifest(FileEntry("a.txt", a)));

        Assert.Equal("keep", await File.ReadAllTextAsync(userFile));
        Assert.True(File.Exists(Path.Combine(link, "stale.txt")));
        Assert.True(File.Exists(_tempDir.FilePath("install", "a.txt")));
    }

    /// <summary>创建目录链接：Linux 用符号链接（无需特权）；Windows CI 无开发者模式，用无需特权的 junction。</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            proc!.WaitForExit();
            Assert.Equal(0, proc.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
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
            // root/CAP_DAC_OVERRIDE 豁免 DAC：去写权限构造不出"删除被拒"形态（同族前提探针）
            if (DacExemptionProbe.Exempt(_tempDir.Path))
            {
                Assert.Skip("当前进程可无视权限位（root/CAP_DAC_OVERRIDE），删除被拒形态不成立");
            }

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
