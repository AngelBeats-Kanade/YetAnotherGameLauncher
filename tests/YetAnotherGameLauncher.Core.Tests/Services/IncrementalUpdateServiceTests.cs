using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.Core.Utilities;
using Xunit;

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
        foreach (var (_, content) in srcs.Concat(dsts))
        {
            _downloader.Responses[Url(patchName)] = patchContent;
        }
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
}
