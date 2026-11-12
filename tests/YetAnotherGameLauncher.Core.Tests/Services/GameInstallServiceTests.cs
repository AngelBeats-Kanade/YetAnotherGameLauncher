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
    public async Task SyncAsync_FastMode_SkipsFullHashForPreexistingFiles()
    {
        // 补档快速路径（2026-10-23）：已有文件只信快速校验（存在性 + 尺寸），本次下载的文件
        // 仍做 MD5 后验；同尺寸损坏的已有文件留给显式“校验修复”，不在补档时顺带全盘重算。
        var existing = "content-a"u8.ToArray();
        var corrupt = "corrupt-X"u8.ToArray();
        Assert.Equal(existing.Length, corrupt.Length);
        await File.WriteAllBytesAsync(_tempDir.FilePath("existing.txt"), corrupt);
        _downloader.Responses[Url("existing.txt")] = existing; // 全量模式会尝试修复；快速模式不应请求
        var added = "content-b!!"u8.ToArray();
        _downloader.Responses[Url("added.txt")] = added;
        var service = new GameInstallService(_downloader);

        var repaired = await service.SyncAsync(
            _tempDir.Path,
            Manifest(FileEntry("existing.txt", existing), FileEntry("added.txt", added)),
            verifyAllExistingFiles: false);

        Assert.Equal(1, repaired);
        Assert.Equal([Url("added.txt")], _downloader.Requests);
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(_tempDir.FilePath("existing.txt")));
        Assert.Equal(added, await File.ReadAllBytesAsync(_tempDir.FilePath("added.txt")));
    }

    [Fact]
    public async Task SyncAsync_FastMode_StillVerifiesDownloadedFiles()
    {
        // 快速路径只跳过"已有文件"的 MD5；本次下载的文件必须仍过 MD5 后验。
        // 篡改内容与清单**同尺寸**：尺寸差异会被最终快速校验兜住，只有 MD5 后验本身能拆穿它
        // （2026-10-23 变异实验实锤：原 8 vs 9 字节形态下拆掉 MD5 后验测试照样绿 = 空洞守卫）。
        var a = "content-a"u8.ToArray();
        var tampered = "tampered!"u8.ToArray();
        Assert.Equal(a.Length, tampered.Length);
        _downloader.Responses[Url("a.txt")] = tampered;
        var service = new GameInstallService(_downloader);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => service.SyncAsync(_tempDir.Path, Manifest(FileEntry("a.txt", a)), verifyAllExistingFiles: false));

        Assert.Contains("Post-download verification failed", ex.Message);
    }

    [Fact]
    public async Task SyncAsync_FastMode_ExistingFileChangedDuringDownload_RepairsItInSecondRound()
    {
        // 补档快速路径的罕见回退（2026-10-23）：下载窗口内已有文件被外部改动/删除时，最终快速校验
        // 会发现缺口 → 把这些文件当第二轮下载补下并 MD5 后验（不动其余已有文件）；缺文件/尺寸不符
        // 的安装不当成功，但无关的同尺寸损坏照旧容忍。
        var existing = "content-a"u8.ToArray();
        await File.WriteAllBytesAsync(_tempDir.FilePath("existing.txt"), existing);
        _downloader.Responses[Url("existing.txt")] = existing;
        var added = "content-b!!"u8.ToArray();
        _downloader.Responses[Url("added.txt")] = added;
        _downloader.OnDownload = request =>
        {
            if (request.Url == Url("added.txt"))
            {
                // 下载窗口内"另一个进程"把已有文件改短：快速校验层（存在 + 尺寸）可见
                File.WriteAllBytes(_tempDir.FilePath("existing.txt"), "x"u8.ToArray());
            }
        };
        var service = new GameInstallService(_downloader);

        var repaired = await service.SyncAsync(
            _tempDir.Path,
            Manifest(FileEntry("existing.txt", existing), FileEntry("added.txt", added)),
            verifyAllExistingFiles: false);

        Assert.Equal(2, repaired); // added.txt + 被改动后补下的 existing.txt
        Assert.Equal(existing, await File.ReadAllBytesAsync(_tempDir.FilePath("existing.txt")));
    }

    [Fact]
    public async Task SyncAsync_FastMode_ConcurrentDamage_DoesNotFailOnUnrelatedCorruptExistingFile()
    {
        // 罕见回退只处理"被并发改动的文件"：无关的同尺寸损坏是快速路径显式容忍的（留给「校验修复」），
        // 不得因为回退触发就把整个补档升级成硬失败——否则一个与本次下载无关的损坏文件会让补档
        // 必败且无自愈路径（2026-10-23 复审）。
        var spare = "spare-ok"u8.ToArray();
        var spareCorrupt = "spareBAD"u8.ToArray();
        Assert.Equal(spare.Length, spareCorrupt.Length);
        await File.WriteAllBytesAsync(_tempDir.FilePath("spare.txt"), spareCorrupt);
        _downloader.Responses[Url("spare.txt")] = spare;

        var victim = "victim-ok"u8.ToArray();
        await File.WriteAllBytesAsync(_tempDir.FilePath("victim.txt"), victim);
        _downloader.Responses[Url("victim.txt")] = victim;

        var added = "content-b!!"u8.ToArray();
        _downloader.Responses[Url("added.txt")] = added;

        var damaged = false;
        _downloader.OnDownload = request =>
        {
            if (request.Url == Url("added.txt") && !damaged)
            {
                damaged = true;
                // 下载窗口内"另一个进程"把 victim 改短（快速校验层可见）→ 触发罕见回退
                File.WriteAllBytes(_tempDir.FilePath("victim.txt"), "x"u8.ToArray());
            }
        };
        var service = new GameInstallService(_downloader);

        var repaired = await service.SyncAsync(
            _tempDir.Path,
            Manifest(
                FileEntry("spare.txt", spare),
                FileEntry("victim.txt", victim),
                FileEntry("added.txt", added)),
            verifyAllExistingFiles: false);

        Assert.Equal(2, repaired); // added.txt + 被改动后补下的 victim.txt
        Assert.Equal(victim, await File.ReadAllBytesAsync(_tempDir.FilePath("victim.txt")));
        Assert.Equal(spareCorrupt, await File.ReadAllBytesAsync(_tempDir.FilePath("spare.txt")));
    }

    [Fact]
    public async Task SyncAsync_EmptyManifest_DoesNotDeleteInstalledFiles()
    {
        // P1 兜底（2026-10-26 复审）：即使渠道层漏检，空清单也不得触发清理把整个安装删光——
        // 清理的语义是"删清单外的游离文件"，空清单下这个语义会退化成"删掉一切"。
        var keep = "installed-content"u8.ToArray();
        await File.WriteAllBytesAsync(_tempDir.FilePath("common.dat"), keep);
        var service = new GameInstallService(_downloader);

        await service.SyncAsync(_tempDir.Path, new GameManifest { Version = "1.0.0", Files = [] });

        Assert.Equal(keep, await File.ReadAllBytesAsync(_tempDir.FilePath("common.dat")));
    }

    [Fact]
    public async Task SyncAsync_ManifestPathWithDotSegment_DoesNotDeleteListedFile()
    {
        // 清理判定与清单边界共用同一条归一串（2026-10-26 复审）：清单里的 `data/./file.dat` 与磁盘上的
        // `data/file.dat` 是同一目标，只比原始串会把清单内文件当游离文件删掉。
        // 直接调 SyncAsync（公开入口），不依赖渠道校验层先拒收这类写法。
        var content = "installed"u8.ToArray();
        Directory.CreateDirectory(_tempDir.FilePath("data"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "file.dat"), content);
        var service = new GameInstallService(_downloader);

        await service.SyncAsync(_tempDir.Path, Manifest(FileEntry("data/./file.dat", content)));

        Assert.Equal(content, await File.ReadAllBytesAsync(_tempDir.FilePath("data", "file.dat")));
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
    public async Task SyncAsync_Downloads_ReportIntermediateByteProgress()
    {
        // 回归（2026-10-02 用户点名"进度条随时更新"）：全量下载同样按字节实时推进——
        // 并行下载的 per-file 字节回调换算为全局增量（此前回调被丢弃、只在文件完成时整量跳变）。
        var content = new byte[100];
        new Random(7).NextBytes(content);
        _downloader.Responses[Url("big.bin")] = content;
        _downloader.ReportProgressInChunks = true;
        var service = new GameInstallService(_downloader);
        var progress = new UpdateProgressCollector();

        await service.SyncAsync(_tempDir.Path, Manifest(FileEntry("big.bin", content)), progress);

        var downloading = progress.Frames.Where(f => f.Phase == UpdatePhase.Downloading).ToList();
        Assert.Contains(downloading, f => f.DownloadedBytes > 0 && f.DownloadedBytes < content.Length);
        Assert.Equal(content.Length, downloading[^1].DownloadedBytes);
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
    public async Task SyncAsync_CleansStaleFilesButPreservesClientSaved()
    {
        // 3.8 更新会在 Client/Saved/Resources/<版本>/ 下新增官方可选包；清单里没有
        // Client/Saved/SaveGames、Logs、Config 等用户数据——同步收尾必须整段保留 Client/Saved，
        // 否则一次跨版本修复就会删掉存档/日志/设置（参考 ww-manager 的 cleanup：跳过任何 Saved/）。
        var a = "content-a"u8.ToArray();
        _downloader.Responses[Url("a.txt")] = a;
        var saved = _tempDir.FilePath("Client", "Saved");
        Directory.CreateDirectory(Path.Combine(saved, "SaveGames"));
        Directory.CreateDirectory(Path.Combine(saved, "Logs"));
        Directory.CreateDirectory(Path.Combine(saved, "Resources", "3.8.0"));
        await File.WriteAllBytesAsync(Path.Combine(saved, "SaveGames", "slot.sav"), [1]);
        await File.WriteAllTextAsync(Path.Combine(saved, "Logs", "Client.log"), "log");
        await File.WriteAllTextAsync(
            Path.Combine(saved, "Resources", "3.8.0", "option.pak"), "option");
        await File.WriteAllTextAsync(_tempDir.FilePath("stray-old.log"), "stray");
        var service = new GameInstallService(_downloader);

        await service.SyncAsync(_tempDir.Path, Manifest(FileEntry("a.txt", a)));

        Assert.True(File.Exists(Path.Combine(saved, "SaveGames", "slot.sav")));
        Assert.True(File.Exists(Path.Combine(saved, "Logs", "Client.log")));
        Assert.True(File.Exists(Path.Combine(saved, "Resources", "3.8.0", "option.pak")));
        Assert.False(File.Exists(_tempDir.FilePath("stray-old.log")));
        Assert.True(File.Exists(_tempDir.FilePath("a.txt")));
    }

    [Fact]
    public async Task SyncAsync_DownloadsVersionedClientSavedResourceAndKeepsExistingSaved()
    {
        // 3.8 全量清单把可选包放在 Client/Saved/Resources/<版本>/：同步要能下载它们，
        // 同时同目录下的 SaveGames/Logs 等用户数据必须原样保留。
        var option = "option-pack"u8.ToArray();
        _downloader.Responses[Url("Client/Saved/Resources/3.8.0/Option/pak.pak")] = option;
        Directory.CreateDirectory(_tempDir.FilePath("Client", "Saved", "SaveGames"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("Client", "Saved", "SaveGames", "slot.sav"), [1]);
        var service = new GameInstallService(_downloader);

        await service.SyncAsync(
            _tempDir.Path,
            Manifest(FileEntry("Client/Saved/Resources/3.8.0/Option/pak.pak", option)));

        Assert.Equal(
            option,
            await File.ReadAllBytesAsync(
                _tempDir.FilePath("Client", "Saved", "Resources", "3.8.0", "Option", "pak.pak")));
        Assert.True(File.Exists(_tempDir.FilePath("Client", "Saved", "SaveGames", "slot.sav")));
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
            if (!DacExemptionProbe.CanConstructDeniedFixture(_tempDir.Path))
            {
                Assert.Skip("探针检出读权限检查被豁免（root/CAP_DAC_OVERRIDE 等能力豁免），拒访形态不可保证构造");
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
