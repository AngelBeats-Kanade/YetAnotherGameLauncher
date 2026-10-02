using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class IncrementalUpdateServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();
    private readonly FakePatchApplier _applier = new();

    public void Dispose() => _tempDir.Dispose();

    private static string Md5(byte[] data) => Hashing.Md5Hex(data);

    private static string Url(string name) => $"https://cdn.example.com/{name}";

    private static ManifestFile FileEntry(string path, byte[] content) =>
        new(path, content.Length, Md5(content), Url: Url(path.Replace('/', '_')));

    private IncrementalUpdateService CreateService() => new(_downloader, _applier);

    private PatchGroup BuildGroup(
        string patchName,
        byte[] patchContent,
        (string Path, byte[] Content)[] srcs,
        (string Path, byte[] Content)[] dsts) => new(
        patchName,
        patchContent.Length,
        Md5(patchContent),
        [.. srcs.Select(s => FileEntry(s.Path, s.Content))],
        [.. dsts.Select(d => FileEntry(d.Path, d.Content))],
        Url(patchName));

    /// <summary>注册假下载器内容并生成对应差分组。</summary>
    private PatchGroup PrepareGroup(
        string patchName,
        (string Path, byte[] Content)[] srcs,
        (string Path, byte[] Content)[] dsts)
    {
        var patchContent = System.Text.Encoding.UTF8.GetBytes($"patch-{patchName}");
        _downloader.Responses[Url(patchName)] = patchContent;
        foreach (var (path, content) in dsts)
        {
            _downloader.Responses[Url(path.Replace('/', '_'))] = content;
        }
        _applier.Outputs[patchName] = dsts.ToDictionary(d => d.Path, d => d.Content);
        return BuildGroup(patchName, patchContent, srcs, dsts);
    }

    [Fact]
    public async Task PredownloadAsync_StagesPatchesFilesAndManifest()
    {
        var src = ("old.dat", "old-content"u8.ToArray());
        var dst = ("new.dat", "new-content"u8.ToArray());
        var group = PrepareGroup("g1.krpdiff", [src], [dst]);
        var manifest = new GameManifest
        {
            Version = "2.0.0",
            Groups = [group],
            Files = [FileEntry("brand-new.pak", "brand"u8.ToArray())],
        };
        _downloader.Responses[Url("brand-new.pak")] = "brand"u8.ToArray();

        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        Assert.True(Directory.Exists(IncrementalUpdateService.PredownloadDir(_tempDir.Path)));
        Assert.True(File.Exists(Path.Combine(IncrementalUpdateService.PredownloadDir(_tempDir.Path), "patches", "g1.krpdiff")));
        Assert.True(File.Exists(Path.Combine(IncrementalUpdateService.PredownloadDir(_tempDir.Path), "files", "brand-new.pak")));
        Assert.NotNull(IncrementalUpdateService.TryLoadStagedManifest(_tempDir.Path));
    }

    [Fact]
    public async Task PredownloadAsync_RerunSkipsAlreadyStagedFiles()
    {
        // 回归（2026-10-02 用户两次重跑实证）：预载失败后重跑不得清空暂存重来——
        // 完好暂存文件按 size+MD5 条目级核验跳过（.temp 由下载器 Range 续传），
        // 只补缺失部分（ww-manager download_incremental 同语义）。
        var group = PrepareGroup("g1.krpdiff", [("old.dat", "old-content"u8.ToArray())], [("new.dat", "new-content"u8.ToArray())]);
        var brandNew = "brand"u8.ToArray();
        var manifest = new GameManifest
        {
            Version = "2.0.0",
            Groups = [group],
            Files = [FileEntry("brand-new.pak", brandNew)],
        };
        _downloader.Responses[Url("brand-new.pak")] = brandNew;
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);
        Assert.Equal(2, _downloader.Requests.Count); // 首跑：1 组 + 1 文件全下载

        _downloader.Requests.Clear();
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        // 重跑：全部条目已在暂存且校验通过 → 零网络请求
        Assert.Empty(_downloader.Requests);
        Assert.NotNull(IncrementalUpdateService.TryLoadStagedManifest(_tempDir.Path));
        var staging = IncrementalUpdateService.PredownloadDir(_tempDir.Path);
        Assert.True(File.Exists(Path.Combine(staging, "patches", "g1.krpdiff")));
        Assert.True(File.Exists(Path.Combine(staging, "files", "brand-new.pak")));
    }

    [Fact]
    public async Task PredownloadAsync_ReportsIntermediateByteProgress()
    {
        // 回归（2026-10-02 用户点名"进度条随时更新"）：进度不再按条目完成跳变——
        // 下载器字节级回调经聚合器实时换算进全局（分块假下载器模拟 64KB 粒度回调）。
        var content = new byte[100];
        new Random(42).NextBytes(content);
        _downloader.Responses[Url("big.bin")] = content;
        _downloader.ReportProgressInChunks = true;
        var manifest = new GameManifest
        {
            Version = "2.0.0",
            Files = [new ManifestFile("big.bin", content.Length, Md5(content), Url("big.bin"))],
        };
        var progress = new UpdateProgressCollector();
        await CreateService().PredownloadAsync(_tempDir.Path, manifest, progress);

        var downloading = progress.Frames.Where(f => f.Phase == UpdatePhase.Downloading).ToList();
        Assert.Contains(downloading, f => f.DownloadedBytes > 0 && f.DownloadedBytes < content.Length);
        Assert.Equal(content.Length, downloading[^1].DownloadedBytes); // 收尾帧 = 全量（同口径）
    }

    [Fact]
    public async Task PredownloadAsync_ManifestPathEscapingSandbox_Rejected()
    {
        // 暂存路径与安装目录同样不可信：../ 拒绝（Windows 上 ..\ 亦然，平台不对称统一按穿越处理）
        var manifest = new GameManifest
        {
            Version = "2.0.0",
            Files = [FileEntry("../evil.txt", "evil"u8.ToArray())],
        };

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().PredownloadAsync(_tempDir.Path, manifest));

        Assert.Contains("escapes", ex.Message);
        Assert.False(File.Exists(_tempDir.FilePath(".yagl", "evil.txt"))); // 未写出暂存目录
    }

    [Fact]
    public void SafeJoin_RootedPath_Rejected()
    {
        Assert.Throws<UpdateException>(() => IncrementalUpdateService.SafeJoin("/staging", "/etc/passwd"));
        Assert.Throws<UpdateException>(() => IncrementalUpdateService.SafeJoin("/staging", "..\\evil.bin"));
    }

    [Fact]
    public async Task ApplyAsync_DeletesListedFilesAfterPatching()
    {
        // 官方 deleteFiles（2026-10-02 真机 6 条旧 pak/sig）：组循环后删除——差分源与废弃清单
        // 存在交集（3.6.1→3.7.0 group_37 有 4 条），先删会毁掉组差分源（真机 P1 实锤，F88 裁定
        // 修订）；不存在/目录条目跳过，清单外文件不动。
        var oldContent = "old-content"u8.ToArray();
        var newContent = "new-content"u8.ToArray();
        Directory.CreateDirectory(_tempDir.FilePath("data"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "file.dat"), oldContent);
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "stale.pak"), "stale"u8.ToArray());
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "keep.dat"), "keep"u8.ToArray());
        Directory.CreateDirectory(_tempDir.FilePath("data", "stale-dir"));
        var group = PrepareGroup("g1.krpdiff", [("data/file.dat", oldContent)], [("data/file.dat", newContent)]);
        var manifest = new GameManifest
        {
            Version = "2.0.0",
            Groups = [group],
            DeleteFiles = ["data/stale.pak", "data/missing.pak", "data/stale-dir"],
        };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        await CreateService().ApplyAsync(_tempDir.Path, manifest);

        Assert.False(File.Exists(_tempDir.FilePath("data", "stale.pak"))); // 点名文件已删
        Assert.True(File.Exists(_tempDir.FilePath("data", "keep.dat"))); // 清单外文件不动
        Assert.True(Directory.Exists(_tempDir.FilePath("data", "stale-dir"))); // 目录条目跳过
    }

    [Fact]
    public async Task ApplyAsync_DeleteFilesIntersectingGroupSrc_AppliesGroupThenDeletes()
    {
        // T1（2026-10-02 真机 P1 实锤）：deleteFiles ∩ group.srcFiles 非空（3.6.1→3.7.0 group_37
        // 的 pakchunk26.sig 等 4 条同时在两清单）——组循环前删除会把差分源自己删掉，组永久失败
        // 且不可自愈。修复后：源文件存活至组差分完成，废弃删除发生在落位之后。
        var staleContent = "stale"u8.ToArray();
        var newContent = "brand-new"u8.ToArray();
        Directory.CreateDirectory(_tempDir.FilePath("data"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "stale.dat"), staleContent);
        var group = PrepareGroup("g1.krpdiff", [("data/stale.dat", staleContent)], [("data/new.dat", newContent)]);
        var manifest = new GameManifest
        {
            Version = "2.0.0",
            Groups = [group],
            DeleteFiles = ["data/stale.dat"],
        };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        await CreateService().ApplyAsync(_tempDir.Path, manifest);

        Assert.Equal(newContent, await File.ReadAllBytesAsync(_tempDir.FilePath("data", "new.dat")));
        Assert.False(File.Exists(_tempDir.FilePath("data", "stale.dat"))); // 废弃删除最终仍执行
    }

    [Fact]
    public async Task ApplyAsync_DeleteFilesEscapingSandbox_Rejected()
    {
        var manifest = new GameManifest
        {
            Version = "2.0.0",
            Groups = [],
            DeleteFiles = ["../outside.txt"],
        };

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().ApplyAsync(_tempDir.Path, manifest));

        Assert.Contains("escapes", ex.Message);
        Assert.False(File.Exists(_tempDir.FilePath("..", "outside.txt")));
    }

    [Fact]
    public async Task ApplyAsync_GroupPathEscapingSandbox_ThrowsUpdateException()
    {
        // 2026-10-02 三轮点名（二轮 review 新立案）：ResolveSafe 对逃逸路径曾抛裸
        // InvalidOperationException（F42 同族——差分组 src/dst 路径穿出 Apply 的
        // "补丁失败统一折算"分类纪律）。统一改抛 UpdateException，本用例钉住组路径口径。
        var group = new PatchGroup(
            "g1.krpdiff",
            10,
            null,
            [new ManifestFile("old.pak", 4, "99999999999999999999999999999999")],
            [new ManifestFile("../evil.pak", 5, "12121212121212121212121212121212")],
            Url("g1.krpdiff"));
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };
        _downloader.Responses[Url("g1.krpdiff")] = "patch-g1"u8.ToArray();
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().ApplyAsync(_tempDir.Path, manifest));

        Assert.Contains("escapes", ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_DeleteFailure_AbortsAfterGroupsApplied()
    {
        // 修订后语义（2026-10-02，F88 裁定随 deleteFiles 挪到组循环后而更新）：删除失败（占用/
        // 只读）仍报错中止，但组差分已完成且 dst 校验通过——游戏内容已是目标版本，被阻断的只是
        // 版本号收尾；重试安全（组幂等跳过 + 删除重试）。
        var oldContent = "old-content"u8.ToArray();
        var newContent = "new-content"u8.ToArray();
        Directory.CreateDirectory(_tempDir.FilePath("data"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "file.dat"), oldContent);
        var lockedPath = _tempDir.FilePath("data", "stale.pak");
        // 锁定目标必须真实存在（Windows CI 实锤：b3c1120 起声明即从未写出，File.Open 直接
        // FileNotFoundException；Linux 腿 Skip 先于锁定行，缺口被完全掩盖）
        await File.WriteAllBytesAsync(lockedPath, "stale"u8.ToArray());
        var group = PrepareGroup("g1.krpdiff", [("data/file.dat", oldContent)], [("data/file.dat", newContent)]);
        var manifest = new GameManifest
        {
            Version = "2.0.0",
            Groups = [group],
            DeleteFiles = ["data/stale.pak"],
        };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);
        // 占用形态只在 Windows 可构造（独占句柄 + FileShare.None 拒删）；
        // Linux 上以只读目录近似不可删形态不稳定，交 Windows 腿守护
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("exclusive handles blocking delete are Windows-only semantics");
        }

        using var lockHandle = File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().ApplyAsync(_tempDir.Path, manifest));

        Assert.Contains("stale.pak", ex.Message);
        Assert.Single(_applier.Calls); // 组差分已完成（删除在其后）
        Assert.Equal(newContent, await File.ReadAllBytesAsync(_tempDir.FilePath("data", "file.dat")));
        Assert.True(File.Exists(lockedPath)); // 删除失败的条目保持原状
    }

    [Fact]
    public async Task ApplyAsync_AppliesGroupsAndReplacesFiles()
    {
        var oldContent = "old-content"u8.ToArray();
        var newContent = "new-content"u8.ToArray();
        Directory.CreateDirectory(_tempDir.FilePath("data"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "file.dat"), oldContent);
        var group = PrepareGroup("g1.krpdiff", [("data/file.dat", oldContent)], [("data/file.dat", newContent)]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        await CreateService().ApplyAsync(_tempDir.Path, manifest);

        Assert.Equal(newContent, await File.ReadAllBytesAsync(_tempDir.FilePath("data", "file.dat")));
        // 补丁器收到的旧目录中有 src 相对路径文件
        var call = Assert.Single(_applier.Calls);
        Assert.Equal(["data/file.dat"], call.OldFiles);
        // 应用成功后暂存目录被清理
        Assert.False(Directory.Exists(IncrementalUpdateService.PredownloadDir(_tempDir.Path)));
    }

    [Fact]
    public async Task ApplyAsync_SkipsGroupWhenDstAlreadyOk()
    {
        var oldContent = "old-content"u8.ToArray();
        var newContent = "new-content"u8.ToArray();
        Directory.CreateDirectory(_tempDir.FilePath("data"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "file.dat"), newContent);
        var group = PrepareGroup("g1.krpdiff", [("data/file.dat", oldContent)], [("data/file.dat", newContent)]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        await CreateService().ApplyAsync(_tempDir.Path, manifest);

        Assert.Empty(_applier.Calls);
    }

    [Fact]
    public async Task ApplyAsync_HeavyWork_NeverCompletesSynchronously()
    {
        // UI 假死守卫（2026-10-02 真机实锤：重入时全部已应用组的 dstFiles 同步 MD5 ≈73GiB
        // 跑在 UI 线程直到首个真 await）：入口让位 + 组校验异步哈希，组合不变量 = 重活不得在
        // 调用线程同步跑完。全组已应用形态下同步完成（IsCompleted）即红。
        var oldContent = "old-content"u8.ToArray();
        var newContent = "new-content"u8.ToArray();
        Directory.CreateDirectory(_tempDir.FilePath("data"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "file.dat"), newContent);
        var group = PrepareGroup("g1.krpdiff", [("data/file.dat", oldContent)], [("data/file.dat", newContent)]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        var task = CreateService().ApplyAsync(_tempDir.Path, manifest);

        Assert.False(task.IsCompleted);
        await task;
        Assert.Empty(_applier.Calls);
    }

    [Fact]
    public async Task ApplyAsync_Progress_VerifyingBeforePatching()
    {
        // 重入进度反馈（假死修复的可见面）：组校验阶段先报 Verifying、实际应用才报 Patching——
        // 重入扫描数十 GiB 期间 UI 有阶段文案而非停留在旧状态（红：现行首帧即 Patching）
        var v1 = "v1"u8.ToArray();
        var v2 = "v2"u8.ToArray();
        var v3 = "v3"u8.ToArray();
        Directory.CreateDirectory(_tempDir.FilePath("data"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "a.dat"), v2); // 组 0 已应用
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "b.dat"), v2); // 组 1 待应用
        var group0 = PrepareGroup("g0.krpdiff", [("data/a.dat", v1)], [("data/a.dat", v2)]);
        var group1 = PrepareGroup("g1.krpdiff", [("data/b.dat", v2)], [("data/b.dat", v3)]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group0, group1] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);
        var progress = new UpdateProgressCollector();

        await CreateService().ApplyAsync(_tempDir.Path, manifest, progress);

        var phases = progress.Frames.Select(f => f.Phase).ToList();
        Assert.Equal(UpdatePhase.Verifying, phases[0]);
        Assert.Contains(UpdatePhase.Patching, phases);
        Assert.Equal(UpdatePhase.Done, phases[^1]);
        Assert.Equal(v3, await File.ReadAllBytesAsync(_tempDir.FilePath("data", "b.dat")));
    }

    [Fact]
    public async Task ApplyAsync_PatchMissing_ThrowsWithGuidance()
    {
        var group = BuildGroup(
            "missing.krpdiff",
            "whatever"u8.ToArray(),
            [("a.dat", "a"u8.ToArray())],
            [("b.dat", "b"u8.ToArray())]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().ApplyAsync(_tempDir.Path, manifest));

        Assert.Contains("was not preloaded", ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_MissingSrcFile_ThrowsWithFullSyncGuidance()
    {
        // 无解析缝（dstUrlResolver 缺省 null，旧调用方/测试向后兼容）时源缺失保持死刑报错：
        // 组级回退（T2-T4）只在显式注入解析缝的调用形态生效
        var group = PrepareGroup(
            "g1.krpdiff",
            [("gone.dat", "old"u8.ToArray())],
            [("new.dat", "new"u8.ToArray())]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().ApplyAsync(_tempDir.Path, manifest));

        Assert.Contains("full update", ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_MissingSources_WithResolver_FallsBackToDirectDownload()
    {
        // T2 组级回退（2026-10-02 真机 P1 实锤的恢复路径）：差分源缺失时经解析缝直下该组产物
        // （size+MD5 校验内建于下载请求，落位复用 dst 校验 + ReplaceWithBackup 原子替换），
        // 更新继续而非死刑——修复 group_37 的 4 个源文件已被此前失败尝试删除的损坏安装
        var newContent = "brand-new"u8.ToArray();
        var group = PrepareGroup("g1.krpdiff", [("data/gone.dat", "old"u8.ToArray())], [("data/new.dat", newContent)]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        await CreateService().ApplyAsync(
            _tempDir.Path, manifest,
            dstUrlResolver: (path, _) => Task.FromResult<string?>(
                path == "data/new.dat" ? Url(path.Replace('/', '_')) : null)); // URL 键位与 FileEntry 约定一致

        Assert.Equal(newContent, await File.ReadAllBytesAsync(_tempDir.FilePath("data", "new.dat")));
        Assert.Empty(_applier.Calls); // 未走 hpatchz：整组经直下自救
    }

    [Fact]
    public async Task ApplyAsync_MissingSources_FallbackMd5Mismatch_ThrowsBeforeReplace()
    {
        // T3 回退产物损坏：下载器（假件不校验 MD5）写入错误字节 → dst 事后校验拦下，
        // 游戏目录不被破坏、报错可操作
        var group = PrepareGroup("g1.krpdiff", [("data/gone.dat", "old"u8.ToArray())], [("data/new.dat", "brand-new"u8.ToArray())]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);
        _downloader.Responses[Url("data/new.dat")] = "corrupt-bytes"u8.ToArray();

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().ApplyAsync(
                _tempDir.Path, manifest,
                dstUrlResolver: (path, _) => Task.FromResult<string?>(Url(path))));

        Assert.Contains("Checksum mismatch", ex.Message);
        Assert.False(File.Exists(_tempDir.FilePath("data", "new.dat")));
    }

    [Fact]
    public async Task ApplyAsync_MissingSources_ResolverReturnsNull_KeepsFullUpdateGuidance()
    {
        // T4 解析缝给不出直链（全量清单查无此路径）→ 维持 "use the full update" 家族的可操作报错
        var group = PrepareGroup("g1.krpdiff", [("data/gone.dat", "old"u8.ToArray())], [("data/new.dat", "new"u8.ToArray())]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().ApplyAsync(
                _tempDir.Path, manifest,
                dstUrlResolver: (_, _) => Task.FromResult<string?>(null)));

        Assert.Contains("not available for direct download", ex.Message); // 解析缝在场但查无此路径的专属报错
        Assert.Contains("full update", ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_MissingSources_ResolverThrows_FoldsToActionableError()
    {
        // review RF-B：解析缝内部故障（全量清单抓取失败等）必须原样穿出、由回退链折算成
        // 「Could not resolve a direct download」可操作报错并携带根因——不能被解析缝吞成 null
        // 走「查无此路径」的误导文案（那会让 CDN/网络故障伪装成清单缺文件）
        var group = PrepareGroup("g1.krpdiff", [("data/gone.dat", "old"u8.ToArray())], [("data/new.dat", "new"u8.ToArray())]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateService().ApplyAsync(
                _tempDir.Path, manifest,
                dstUrlResolver: (_, _) => throw new InvalidOperationException("manifest fetch failed")));

        Assert.Contains("Could not resolve a direct download", ex.Message);
        Assert.Contains("manifest fetch failed", ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_CorruptApplierOutput_ThrowsBeforeReplace()
    {
        var oldContent = "old-content"u8.ToArray();
        var newContent = "new-content"u8.ToArray();
        Directory.CreateDirectory(_tempDir.FilePath("data"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "file.dat"), oldContent);
        var group = PrepareGroup("g1.krpdiff", [("data/file.dat", oldContent)], [("data/file.dat", newContent)]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);
        _applier.CorruptOutput = true;

        await Assert.ThrowsAsync<UpdateException>(() => CreateService().ApplyAsync(_tempDir.Path, manifest));

        // 游戏本体未被破坏
        Assert.Equal(oldContent, await File.ReadAllBytesAsync(_tempDir.FilePath("data", "file.dat")));
    }

    [Fact]
    public async Task ApplyAsync_ReplaceFailure_RollsBackWholeGroup()
    {
        var oldContent = "old-content"u8.ToArray();
        var newContent = "new-content"u8.ToArray();
        Directory.CreateDirectory(_tempDir.FilePath("data"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "file.dat"), oldContent);
        // data2/blocked.dat 的父目录被一个同名文件占据，替换必然失败
        Directory.CreateDirectory(_tempDir.FilePath("data2"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data2", "blocked.dat"), "blocker"u8.ToArray());

        var group = PrepareGroup(
            "g1.krpdiff",
            [("data/file.dat", oldContent)],
            [("data/file.dat", newContent), ("data2/blocked.dat/inner.dat", newContent)]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        await Assert.ThrowsAsync<UpdateException>(() => CreateService().ApplyAsync(_tempDir.Path, manifest));

        // 第一个文件已替换成功但整组回滚：恢复为旧内容，且没有 .yagl-bak 残留
        Assert.Equal(oldContent, await File.ReadAllBytesAsync(_tempDir.FilePath("data", "file.dat")));
        Assert.Empty(Directory.EnumerateFiles(_tempDir.Path, "*.yagl-bak", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ApplyAsync_SecondGroupFailure_KeepsFirstGroupApplied()
    {
        var v1 = "v1"u8.ToArray();
        var v2 = "v2"u8.ToArray();
        var v3 = "v3"u8.ToArray();
        await File.WriteAllBytesAsync(_tempDir.FilePath("a.dat"), v1);
        await File.WriteAllBytesAsync(_tempDir.FilePath("b.dat"), v2);

        var group1 = PrepareGroup("g1.krpdiff", [("a.dat", v1)], [("a.dat", v2)]);
        var group2 = PrepareGroup("g2.krpdiff", [("b.dat", v2)], [("b.dat", v3)]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group1, group2] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);
        _applier.FailOnCallIndex = 1; // 第二组补丁阶段失败

        await Assert.ThrowsAsync<UpdateException>(() => CreateService().ApplyAsync(_tempDir.Path, manifest));

        // 第一组已生效
        Assert.Equal(v2, await File.ReadAllBytesAsync(_tempDir.FilePath("a.dat")));
        // 第二组未生效（回滚后保持原值）
        Assert.Equal(v2, await File.ReadAllBytesAsync(_tempDir.FilePath("b.dat")));
    }

    [Fact]
    public void ReplaceWithBackup_PlaceMoveFails_InFlightEntryRestored()
    {
        // 回归：落位 Move（新文件 → 目标）失败时该条目若未登记，回滚会遗漏它——
        // 组内留下缺失文件、原内容孤悬 .yagl-bak，重试只能整包重下。
        // 构造：a.dat 正常落位；b.dat 原文存在但 newdir 里没有产物，其落位 Move 必然失败。
        var oldA = "old-a"u8.ToArray();
        var newA = "new-a"u8.ToArray();
        var oldB = "old-b"u8.ToArray();
        var newDir = Path.Combine(_tempDir.Path, "newdir");
        Directory.CreateDirectory(newDir);
        File.WriteAllBytes(_tempDir.FilePath("a.dat"), oldA);
        File.WriteAllBytes(_tempDir.FilePath("b.dat"), oldB);
        File.WriteAllBytes(Path.Combine(newDir, "a.dat"), newA);

        var group = new PatchGroup(
            "g1.krpdiff", 1, "x",
            [FileEntry("a.dat", oldA), FileEntry("b.dat", oldB)],
            [FileEntry("a.dat", newA), FileEntry("b.dat", newA)],
            Url("g1.krpdiff"));

        var ex = Assert.Throws<UpdateException>(
            () => IncrementalUpdateService.ReplaceWithBackup(_tempDir.Path, group, newDir));

        Assert.Contains("rolled back", ex.Message);
        // 两个文件都必须还原为旧内容（修复前 b.dat 缺失、oldB 孤悬 b.dat.yagl-bak）
        Assert.Equal(oldA, File.ReadAllBytes(_tempDir.FilePath("a.dat")));
        Assert.Equal(oldB, File.ReadAllBytes(_tempDir.FilePath("b.dat")));
        Assert.Empty(Directory.EnumerateFiles(_tempDir.Path, "*.yagl-bak", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task StagedManifest_RoundTrips()
    {
        var group = PrepareGroup(
            "g1.krpdiff",
            [("old.dat", "old"u8.ToArray())],
            [("new.dat", "new"u8.ToArray())]);
        var manifest = new GameManifest
        {
            Version = "2.0.0",
            Groups = [group],
            Files = [FileEntry("x.pak", "xxx"u8.ToArray())],
        };
        _downloader.Responses[Url("x.pak")] = "xxx"u8.ToArray();

        await CreateService().PredownloadAsync(_tempDir.Path, manifest);
        var loaded = IncrementalUpdateService.TryLoadStagedManifest(_tempDir.Path);

        Assert.NotNull(loaded);
        Assert.Equal("2.0.0", loaded.Version);
        var loadedGroup = Assert.Single(loaded.Groups);
        Assert.Equal("g1.krpdiff", loadedGroup.PatchFile);
        Assert.Equal("new.dat", Assert.Single(loadedGroup.DstFiles).Path);
        Assert.NotNull(loadedGroup.Url);
    }

    [Fact]
    public void TryLoadStagedManifest_CorruptJson_ReturnsNull()
    {
        // 审计缺口（2026-09-19）：暂存清单写一半崩溃/损坏 → 按无暂存兜底（HasStagedPredownload=false），
        // 唤起页与主操作按钮不得因 JsonException 崩溃
        var staging = IncrementalUpdateService.PredownloadDir(_tempDir.Path);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "manifest.json"), "{broken");

        Assert.Null(IncrementalUpdateService.TryLoadStagedManifest(_tempDir.Path));
    }

    [Fact]
    public void TryDeleteBackup_UndeletableFile_ReturnsFalseWithoutThrowing()
    {
        // 回归（2026-09-20）：更新成功后的 .yagl-bak 清理曾被裸 IOException/UnauthorizedAccess 打穿——
        // Windows 杀软锁住备份文件时，已完成且校验通过的更新被整体推翻且 state.json 不更新。
        // 删除失败必须尽力而为：返回 false、不抛。
        // 不可删除构造：Windows 置只读属性；POSIX 去掉所在目录写位
        var dir = Path.Combine(_tempDir.Path, "bak-dir");
        Directory.CreateDirectory(dir);
        var backup = Path.Combine(dir, "game.dll.yagl-bak");
        File.WriteAllText(backup, "old");

        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(backup, FileAttributes.ReadOnly);
        }
        else
        {
            // root/CAP_DAC_OVERRIDE 豁免 DAC：去写权限构造不出"删不掉"形态（同族前提探针）
            if (!DacExemptionProbe.CanConstructDeniedFixture(_tempDir.Path))
            {
                Assert.Skip("探针检出读权限检查被豁免（root/CAP_DAC_OVERRIDE 等能力豁免），拒访形态不可保证构造");
            }

            // 删除依赖所在目录写位：去掉写位使 File.Delete 抛 UnauthorizedAccessException
            new DirectoryInfo(dir) { UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserExecute };
        }

        try
        {
            Assert.False(IncrementalUpdateService.TryDeleteBackup(backup));
            Assert.True(File.Exists(backup));
        }
        finally
        {
            // 恢复可写，避免 TempDir 清理失败
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(backup, FileAttributes.Normal);
            }
            else
            {
                new DirectoryInfo(dir) { UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute };
            }
        }
    }

    [Fact]
    public async Task ApplyAsync_UnreadableSourceFile_ClassifiedAsUpdateException()
    {
        // F67：ApplyGroupAsync 的 File.Copy 位于折算 try 之外——源文件拒读/被占用（POSIX 权限
        // 剥夺、Windows 杀软独占锁定）抛出的裸异常不经 UpdateException 包装穿出，与同方法
        // "补丁失败统一折算 UpdateException"的分类纪律不一致。
        // src 与 dst 必须是不同路径（Windows CI 首跑实锤：同路径时独占锁先在 ApplyAsync 前置的
        // GroupAlreadyApplied→CheckFile→Md5Hex 读 dst 处爆裸 IOException，到不了被测的 File.Copy）
        var oldContent = "old-content"u8.ToArray();
        var newContent = "new-content"u8.ToArray();
        Directory.CreateDirectory(_tempDir.FilePath("data"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "source.dat"), oldContent);
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "target.dat"), oldContent);
        var group = PrepareGroup("g1.krpdiff", [("data/source.dat", oldContent)], [("data/target.dat", newContent)]);
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        if (OperatingSystem.IsWindows())
        {
            // 源文件独占锁（FileShare.None）：File.Copy 打不开源 → IOException
            await using var lockStream = new FileStream(
                _tempDir.FilePath("data", "source.dat"), FileMode.Open, FileAccess.Read, FileShare.None);
            await Assert.ThrowsAsync<UpdateException>(
                () => CreateService().ApplyAsync(_tempDir.Path, manifest));
        }
        else
        {
            // root/CAP_DAC_READ_SEARCH 读豁免：拒读形态不可保证构造（DacExemptionProbe 同族前提）
            if (!DacExemptionProbe.CanConstructDeniedFixture(_tempDir.Path))
            {
                Assert.Skip("探针检出读权限检查被豁免（root/CAP_DAC_OVERRIDE 等能力豁免），拒读形态不可保证构造");
            }

            new FileInfo(_tempDir.FilePath("data", "source.dat"))
            {
                UnixFileMode = UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            };
            await Assert.ThrowsAsync<UpdateException>(
                () => CreateService().ApplyAsync(_tempDir.Path, manifest));
        }
    }

    [Fact]
    public async Task ApplyAsync_CancellationBeforeStagedFilePlacement_ThrowsOperationCanceled()
    {
        // 落位循环此前无取消检查点：大库逐文件 MD5 阶段取消无响应（2026-09-20 复审补齐）
        var manifest = new GameManifest
        {
            Version = "2.0.0",
            Files = [FileEntry("a.pak", "aaa"u8.ToArray()), FileEntry("b.pak", "bbb"u8.ToArray())],
        };
        var staging = IncrementalUpdateService.PredownloadDir(_tempDir.Path);
        Directory.CreateDirectory(Path.Combine(staging, "files"));
        await File.WriteAllTextAsync(Path.Combine(staging, "files", "a.pak"), "aaa");
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ApplyAsync(_tempDir.Path, manifest, cancellationToken: cts.Token));
    }
}

public class IncrementalUpdateServiceStagedManifestTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public void TryLoadStagedManifest_UnreadableFile_ReturnsNullInsteadOfThrowing()
    {
        // 回归（2026-09-20 复审）：manifest.json 读不了（占用/无权限）按"暂存未知"处理返回 null，
        // 与 LocalStateService.Load 同语义——只捕 JsonException 会让异常穿出 RefreshAsync
        // 的弃元调用点，静默丢失状态刷新与完成提示
        var staging = IncrementalUpdateService.PredownloadDir(_tempDir.Path);
        Directory.CreateDirectory(staging);
        var manifestPath = Path.Combine(staging, "manifest.json");
        File.WriteAllText(manifestPath, "{}");
        if (!OperatingSystem.IsWindows())
        {
            // root/CAP_DAC_OVERRIDE 豁免 DAC：拒读形态构造不出（同族前提探针）
            if (!DacExemptionProbe.CanConstructDeniedFixture(_tempDir.Path))
            {
                Assert.Skip("当前进程可无视权限位（root/CAP_DAC_OVERRIDE 等能力豁免），拒读形态不成立");
            }

            File.SetUnixFileMode(manifestPath, UnixFileMode.None);
        }
        else
        {
            Assert.Skip("Unix file modes are unavailable on Windows; the locked-file scenario is covered on the Linux leg.");
        }

        Assert.Null(IncrementalUpdateService.TryLoadStagedManifest(_tempDir.Path));
    }
}

public class IncrementalUpdateHygieneTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();
    private readonly FakePatchApplier _applier = new();

    public void Dispose() => _tempDir.Dispose();

    private IncrementalUpdateService CreateService() => new(_downloader, _applier);

    private static string Url(string name) => $"https://cdn.example.com/{name}";

    [Fact]
    public async Task ApplyAsync_Success_RemovesPatchWorkDirectory()
    {
        // 回归（2026-09-20 三审）：差分工作草稿（SrcFiles 完整副本，可达数 GB~数十 GB）曾只在
        // 下一次 Apply 开头清理——游戏停更或改走全量后永久滞留；.yagl 在安装同步保留名单里不会被游走清理
        var oldContent = "old-content"u8.ToArray();
        var newContent = "new-content"u8.ToArray();
        Directory.CreateDirectory(_tempDir.FilePath("data"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "file.dat"), oldContent);
        var patch = System.Text.Encoding.UTF8.GetBytes("patch-g1");
        _downloader.Responses[Url("g1.krpdiff")] = patch;
        _applier.Outputs["g1.krpdiff"] = new Dictionary<string, byte[]> { ["data/file.dat"] = newContent };
        var group = new PatchGroup(
            "g1.krpdiff", patch.Length, YetAnotherGameLauncher.Core.Utilities.Hashing.Md5Hex(newContent),
            SrcFiles: [new ManifestFile("data/file.dat", oldContent.Length, YetAnotherGameLauncher.Core.Utilities.Hashing.Md5Hex(oldContent))],
            DstFiles: [new ManifestFile("data/file.dat", newContent.Length, YetAnotherGameLauncher.Core.Utilities.Hashing.Md5Hex(newContent))],
            Url("g1.krpdiff"));
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };

        await CreateService().PredownloadAsync(_tempDir.Path, manifest);
        await CreateService().ApplyAsync(_tempDir.Path, manifest);

        Assert.Equal(newContent, await File.ReadAllBytesAsync(_tempDir.FilePath("data", "file.dat")));
        Assert.False(Directory.Exists(Path.Combine(_tempDir.Path, ".yagl", "patchwork")));
    }

    [Fact]
    public async Task ApplyAsync_CancelledWithGroups_ThrowsOperationCanceled()
    {
        // 组循环顶部的取消检查点（此前只有文件循环检查点有测试）
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var patch = System.Text.Encoding.UTF8.GetBytes("patch-g1");
        _downloader.Responses[Url("g1.krpdiff")] = patch;
        var group = new PatchGroup(
            "g1.krpdiff", patch.Length, "0".PadLeft(32, '0'),
            SrcFiles: [new ManifestFile("data/file.dat", 1, "1".PadLeft(32, '1'))],
            DstFiles: [new ManifestFile("data/file.dat", 1, "2".PadLeft(32, '2'))],
            Url("g1.krpdiff"));
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };
        await CreateService().PredownloadAsync(_tempDir.Path, manifest);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateService().ApplyAsync(_tempDir.Path, manifest, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ApplyAsync_ReadOnlyTarget_Windows_ReplacedWithGuidance()
    {
        // 回归（2026-09-20 三审）：ReplaceWithBackup 曾不解只读属性——Windows 上只读游戏文件
        // 让增量更新永久卡死（Move 抛、回滚 Delete 也抛，重试永不自愈）。
        // Linux rename 不受目标只读位影响，此用例仅在 Windows 腿有意义
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("read-only rename semantics are Windows-only (Linux rename ignores the target's read-only bit)");
        }

        var oldContent = "old-content"u8.ToArray();
        var newContent = "new-content"u8.ToArray();
        Directory.CreateDirectory(_tempDir.FilePath("data"));
        await File.WriteAllBytesAsync(_tempDir.FilePath("data", "file.dat"), oldContent);
        File.SetAttributes(_tempDir.FilePath("data", "file.dat"), FileAttributes.ReadOnly);
        var patch = System.Text.Encoding.UTF8.GetBytes("patch-g1");
        _downloader.Responses[Url("g1.krpdiff")] = patch;
        _applier.Outputs["g1.krpdiff"] = new Dictionary<string, byte[]> { ["data/file.dat"] = newContent };
        var group = new PatchGroup(
            "g1.krpdiff", patch.Length, YetAnotherGameLauncher.Core.Utilities.Hashing.Md5Hex(newContent),
            SrcFiles: [new ManifestFile("data/file.dat", oldContent.Length, YetAnotherGameLauncher.Core.Utilities.Hashing.Md5Hex(oldContent))],
            DstFiles: [new ManifestFile("data/file.dat", newContent.Length, YetAnotherGameLauncher.Core.Utilities.Hashing.Md5Hex(newContent))],
            Url("g1.krpdiff"));
        var manifest = new GameManifest { Version = "2.0.0", Groups = [group] };

        await CreateService().PredownloadAsync(_tempDir.Path, manifest);
        await CreateService().ApplyAsync(_tempDir.Path, manifest);

        Assert.Equal(newContent, await File.ReadAllBytesAsync(_tempDir.FilePath("data", "file.dat")));
    }
}
