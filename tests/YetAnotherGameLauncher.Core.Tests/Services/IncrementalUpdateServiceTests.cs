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
        var staging = IncrementalUpdateService.ResetStaging(_tempDir.Path);
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
