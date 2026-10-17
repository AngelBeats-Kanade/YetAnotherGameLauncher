using System.Runtime.InteropServices;
using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

[Collection("sequential")]
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

    [Fact]
    public void ExtractArchive_MissingArchive_ClassifiedAsUpdateException()
    {
        // D1：体量上限取 FileInfo(archivePath).Length 位于折算 try 之外——归档被删/路径拼错
        // 的裸 FNFE 穿出（依赖链落 deps_error_unexpected），与"除取消外全部折算"的纪律不一致。
        // FNFE ⊂ IOException，进 try 即被既有 catch 折算
        Assert.Throws<UpdateException>(() => PackageInstallerService.ExtractArchive(
            _tempDir.FilePath("missing.pkg"), _tempDir.FilePath("install"), "测试包"));
    }

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
    public async Task Predownload_AllPackagesAlreadyStaged_FinalFrameCarriesTotalBytes()
    {
        // 二轮 review（2026-10-02）：重跑全跳过路径的末帧必须精确到 totalBytes——
        // 跳过路径的 Add 若被 100ms 节流吞掉且无 ForceReport，末帧字节停在最后一个
        // 过节流的投递值上，进度条不满格直到 Done。
        var zip1 = TestZip.Create(("game-a.exe", "MZ-stub-a"));
        var zip2 = TestZip.Create(("game-b.exe", "MZ-stub-b"));
        _downloader.Responses[ZipUrl] = zip1;
        _downloader.Responses["https://cdn.example.com/game-1.zip"] = zip2;
        var manifest = new GameManifest
        {
            Version = "1.3.0",
            EntriesAreArchives = true,
            Files =
            [
                new ManifestFile("game-0.zip", zip1.Length, Hashing.Md5Hex(zip1), Url: ZipUrl),
                new ManifestFile("game-1.zip", zip2.Length, Hashing.Md5Hex(zip2), Url: "https://cdn.example.com/game-1.zip"),
            ],
        };
        // 首跑铺好暂存，重跑时全部跳过
        await new PackageInstallerService(_downloader).PredownloadAsync(_tempDir.Path, manifest);
        _downloader.Requests.Clear();
        var progress = new UpdateProgressCollector();

        await new PackageInstallerService(_downloader).PredownloadAsync(_tempDir.Path, manifest, progress);

        Assert.Empty(_downloader.Requests); // 全部跳过
        var downloading = progress.Frames.Where(f => f.Phase == UpdatePhase.Downloading).ToList();
        Assert.Equal(zip1.Length + zip2.Length, downloading[^1].DownloadedBytes);
    }

    [Fact]
    public async Task Predownload_Downloads_ReportIntermediateByteProgress()
    {
        // 回归（2026-10-02 用户点名"进度条随时更新"）：包式下载同样按字节实时推进
        //（此前给下载器传 null 进度，只在整包完成时跳变——几十 GB 的包期间进度条纹丝不动）。
        var zip = TestZip.Create(("game.exe", "MZ-stub-payload-0123456789abcdefgh"));
        _downloader.Responses[ZipUrl] = zip;
        _downloader.ReportProgressInChunks = true;
        var progress = new UpdateProgressCollector();

        await new PackageInstallerService(_downloader).PredownloadAsync(_tempDir.Path, ManifestFor(zip), progress);

        var downloading = progress.Frames.Where(f => f.Phase == UpdatePhase.Downloading).ToList();
        Assert.Contains(downloading, f => f.DownloadedBytes > 0 && f.DownloadedBytes < zip.Length);
        Assert.Equal(zip.Length, downloading[^1].DownloadedBytes);
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
    public async Task Predownload_ReusesIntactStagedPackages_OnRetry()
    {
        // 回归（2026-09-20 复审）：包式预下载曾每次先整删暂存目录再全量重下——清单写入
        // 失败或中断后重试会把数十 GB 已下载内容全部作废。现在已完整暂存的包直接复用
        _downloader.Responses[ZipUrl] = ZipBytes;
        var service = new PackageInstallerService(_downloader);

        await service.PredownloadAsync(_tempDir.Path, Manifest());
        var requestsAfterFirst = _downloader.Requests.Count;

        await service.PredownloadAsync(_tempDir.Path, Manifest());

        Assert.Equal(requestsAfterFirst, _downloader.Requests.Count);
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
    public async Task InstallAsync_DirectoryEntryEscapingSandbox_ThrowsAndCreatesNothing()
    {
        // 回归：目录条目（以 / 结尾）曾先于 .. 校验早退，恶意包可在安装目录外建目录
        var zip = TestZip.Create(("../evil_dir/", ""), ("ok.txt", "ok"));
        _downloader.Responses[ZipUrl] = zip;

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => new PackageInstallerService(_downloader).InstallAsync(_tempDir.Path, ManifestFor(zip)));

        Assert.Contains("escapes", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_tempDir.Path, "..", "evil_dir")));
        Assert.False(File.Exists(_tempDir.FilePath("ok.txt"))); // 恶意条目在前，后续条目不再解压
    }

    [Fact]
    public async Task InstallAsync_RootedDirectoryEntry_IsContainedInInstallDir()
    {
        // 回归：首部 / 的目录条目曾把未清洗名喂给 Path.Combine（第二参 rooted 时原样返回），
        // 在安装目录之外（盘根/文件系统根）建目录；现与文件条目同规则剥离后收容
        var zip = TestZip.Create(("/evil_dir/", ""), ("ok.txt", "ok"));
        _downloader.Responses[ZipUrl] = zip;

        await new PackageInstallerService(_downloader).InstallAsync(_tempDir.Path, ManifestFor(zip));

        Assert.Equal("ok", await File.ReadAllTextAsync(_tempDir.FilePath("ok.txt")));
        Assert.True(Directory.Exists(_tempDir.FilePath("evil_dir"))); // 收容到安装目录内
        Assert.False(Directory.Exists(Path.Combine(
            Path.GetPathRoot(Path.GetFullPath(_tempDir.Path))!, "evil_dir")));
    }

    [Fact]
    public async Task InstallAsync_BenignDirectoryEntries_StillExtractFiles()
    {
        // 正常打包器的目录条目不受沙箱校验收紧影响
        var zip = TestZip.Create(("SubDir/", ""), ("SubDir/file.txt", "inside"));
        _downloader.Responses[ZipUrl] = zip;

        await new PackageInstallerService(_downloader).InstallAsync(_tempDir.Path, ManifestFor(zip));

        Assert.Equal("inside", await File.ReadAllTextAsync(_tempDir.FilePath("SubDir", "file.txt")));
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

/// <summary>
/// 包式渠道的分卷归档（终末地 packs 实测形态，2026-10-17 国服/国际服 1.6.4 均 59 卷）：
/// 同名归档的 <c>.zip.001/.002/…</c> 各卷只是字节切片，单卷不是合法 zip（中央目录与
/// EOCD 在末卷）——必须按分卷顺序拼接成一个归档再解压。
/// </summary>
[Collection("sequential")]
public class PackageInstallerSplitVolumeTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();

    public void Dispose() => _tempDir.Dispose();

    private static readonly byte[] ZipBytes = TestZip.Create(
        ("game.exe", "MZ-stub-split-volume-payload"),
        ("data/config.ini", "cfg=1"));

    private static string VolumeUrl(int number) => $"https://cdn.example.com/game.zip.{number:D3}";

    /// <summary>把归档字节切成连续分卷（尽量均分、末卷收尾），模拟 CDN 的 .001/.002… 形态。</summary>
    private static byte[][] SplitIntoVolumes(byte[] archive, int volumeCount)
    {
        var size = (archive.Length + volumeCount - 1) / volumeCount;
        var volumes = new byte[volumeCount][];
        for (var i = 0; i < volumeCount; i++)
        {
            var start = Math.Min(archive.Length, i * size);
            volumes[i] = archive[start..Math.Min(archive.Length, start + size)];
        }

        return volumes;
    }

    private static GameManifest SplitVolumeManifest(byte[][] volumes) => new()
    {
        Version = "1.6.4",
        EntriesAreArchives = true,
        ArchivesAreSplitVolumes = true,
        Files =
        [
            .. volumes.Select((volume, index) => new ManifestFile(
                $"game.zip.{index + 1:D3}", volume.Length, Hashing.Md5Hex(volume), Url: VolumeUrl(index + 1))),
        ],
    };

    private void ServeVolumes(byte[][] volumes)
    {
        for (var i = 0; i < volumes.Length; i++)
        {
            _downloader.Responses[VolumeUrl(i + 1)] = volumes[i];
        }
    }

    [Fact]
    public async Task InstallAsync_SplitVolumes_ExtractsConcatenatedArchiveOnce()
    {
        // 用户实报（终末地 1.6.4 更新，59 卷 ≈ 58 GiB）：逐卷当独立 zip 解压，首卷必然
        // "End of Central Directory record could not be found"（EOCD 只在末卷）。
        // 红证据即该 UpdateException；修复 = 按分卷顺序拼接后整体解压一次
        var volumes = SplitIntoVolumes(ZipBytes, 3);
        ServeVolumes(volumes);

        var ex = await Record.ExceptionAsync(() =>
            new PackageInstallerService(_downloader).InstallAsync(_tempDir.Path, SplitVolumeManifest(volumes)));

        Assert.Null(ex);
        Assert.Equal("MZ-stub-split-volume-payload", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe")));
        Assert.Equal("cfg=1", await File.ReadAllTextAsync(_tempDir.FilePath("data", "config.ini")));
        Assert.False(Directory.Exists(Path.Combine(_tempDir.Path, ".yagl", "packages"))); // 分卷随包目录清理
    }

    [Fact]
    public void ExtractSplitArchive_NoVolumes_ThrowsUpdateException()
    {
        // 空分卷集（手改暂存清单等形态）：折算成可操作报错，不给裸 ArgumentException
        var ex = Record.Exception(() => PackageInstallerService.ExtractSplitArchive([], _tempDir.Path));

        Assert.IsType<UpdateException>(ex);
        Assert.Contains("no volumes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyPredownload_SplitVolumes_ExtractsStagedVolumesWithoutReDownload()
    {
        // 两段式（预下载 → 应用）同形态：暂存清单 JSON 往返必须保留分卷标记，否则应用阶段
        // 又退回逐卷解压
        var volumes = SplitIntoVolumes(ZipBytes, 3);
        ServeVolumes(volumes);
        var service = new PackageInstallerService(_downloader);

        await service.PredownloadAsync(_tempDir.Path, SplitVolumeManifest(volumes));
        var staged = IncrementalUpdateService.TryLoadStagedManifest(_tempDir.Path);
        Assert.NotNull(staged);
        Assert.True(staged.ArchivesAreSplitVolumes);
        var requestsAfterPredownload = _downloader.Requests.Count;

        var ex = await Record.ExceptionAsync(() => service.ApplyPredownloadAsync(_tempDir.Path, staged));

        Assert.Null(ex);
        Assert.Equal(requestsAfterPredownload, _downloader.Requests.Count); // 应用阶段零下载
        Assert.Equal("MZ-stub-split-volume-payload", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe")));
        Assert.False(Directory.Exists(IncrementalUpdateService.PredownloadDir(_tempDir.Path)));
    }

    [Fact]
    public async Task ApplyPredownload_StaleManifestWithoutSplitFlag_RestoresSplitExtraction()
    {
        // review 非阻塞 3（2026-10-17）：旧版本写入的暂存清单没有分卷标记（字段缺省 = false），
        // 应用阶段会退回逐卷解压——正是用户报障形态，而此刻几十 GiB 已下载完。
        // 安装器按同一规则复算：认定分卷 + 按序号重排（这里卷序故意是"响应顺序"），零下载救回
        var volumes = SplitIntoVolumes(ZipBytes, 3);
        ServeVolumes(volumes);
        var service = new PackageInstallerService(_downloader);

        Directory.CreateDirectory(PackageInstallerService.PackagesDir(_tempDir.Path));
        for (var i = 0; i < volumes.Length; i++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(PackageInstallerService.PackagesDir(_tempDir.Path), $"game.zip.{i + 1:D3}"), volumes[i]);
        }

        var responseOrder = new[] { 3, 1, 2 };
        var stale = new GameManifest
        {
            Version = "1.6.4",
            EntriesAreArchives = true,
            Files =
            [
                .. responseOrder.Select(number => new ManifestFile(
                    $"game.zip.{number:D3}", volumes[number - 1].Length, Hashing.Md5Hex(volumes[number - 1]),
                    Url: VolumeUrl(number))),
            ],
        };
        await IncrementalUpdateService.WriteStagedManifestAsync(
            IncrementalUpdateService.PredownloadDir(_tempDir.Path), stale, CancellationToken.None);
        var reloaded = IncrementalUpdateService.TryLoadStagedManifest(_tempDir.Path);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.ArchivesAreSplitVolumes); // 旧形态：字段缺省 = false
        var requestsBefore = _downloader.Requests.Count;

        var ex = await Record.ExceptionAsync(() => service.ApplyPredownloadAsync(_tempDir.Path, reloaded));

        Assert.Null(ex);
        Assert.Equal(requestsBefore, _downloader.Requests.Count); // 已下载的几十 GiB 被复用，零下载
        Assert.Equal("MZ-stub-split-volume-payload", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe")));
        Assert.Equal("cfg=1", await File.ReadAllTextAsync(_tempDir.FilePath("data", "config.ini")));
    }

    [Fact]
    public async Task ApplyPredownload_StaleManifestWithGappedVolumes_ReportsActionableError()
    {
        // 像分卷但序号不连续（.001/.003）：拼接结果必定不是合法归档——在下载/解压前明确报错，
        // 而不是等解压阶段撞上无分类的 "End of Central Directory" 报错
        var volumes = SplitIntoVolumes(ZipBytes, 3);
        ServeVolumes(volumes);
        var gapped = new GameManifest
        {
            Version = "1.6.4",
            EntriesAreArchives = true,
            Files =
            [
                new ManifestFile("game.zip.001", volumes[0].Length, Hashing.Md5Hex(volumes[0]), Url: VolumeUrl(1)),
                new ManifestFile("game.zip.003", volumes[2].Length, Hashing.Md5Hex(volumes[2]), Url: VolumeUrl(3)),
            ],
        };

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => new PackageInstallerService(_downloader).ApplyPredownloadAsync(_tempDir.Path, gapped));

        Assert.Contains("not a contiguous single set", ex.Message);
        Assert.Empty(_downloader.Requests); // 一处不落盘、一个字节不下
    }
}

/// <summary>
/// 包式安装的旧版残留清理：官方清单点名的废弃文件（终末地 delete_files.txt）在解压完成后删除，
/// 并回收腾空的目录；只删归档产物之外的文件（RF-C 同纪律），路径必须落在安装目录内。
/// </summary>
[Collection("sequential")]
public class PackageInstallerObsoleteFilesTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();

    public void Dispose() => _tempDir.Dispose();

    private const string ZipUrl = "https://cdn.example.com/game-0.zip";

    private static readonly byte[] ZipBytes = TestZip.Create(("game.exe", "MZ-stub"));

    private static GameManifest Manifest() => new()
    {
        Version = "1.6.4",
        EntriesAreArchives = true,
        Files = [new ManifestFile("game.zip", ZipBytes.Length, Hashing.Md5Hex(ZipBytes), Url: ZipUrl)],
    };

    private string CreateStaleFile(string relative)
    {
        var path = _tempDir.FilePath([.. relative.Replace('/', Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar)]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "stale");
        return path;
    }

    [Fact]
    public async Task InstallAsync_EmptyManifestWithObsoleteList_DoesNotDeleteAnything()
    {
        // review P2-2（2026-10-17）：清单为空（服务器异常/空转）时绝不按废弃清单清理——
        // 否则安装目录被清成半空、版本号还会被写新
        var stale = CreateStaleFile("Endfield_Data/StreamingAssets/VFS/AAAA/old.chk");
        var emptyManifest = new GameManifest
        {
            Version = "1.6.4",
            EntriesAreArchives = true,
            Files = [],
        };

        await new PackageInstallerService(_downloader).InstallAsync(
            _tempDir.Path, emptyManifest, obsoleteFiles: ["Endfield_Data/StreamingAssets/VFS/AAAA/old.chk"]);

        Assert.True(File.Exists(stale));
        Assert.Empty(_downloader.Requests);
    }

    [Fact]
    public async Task InstallAsync_ObsoleteFiles_DeletedAfterExtraction_AndEmptyDirectoriesPruned()
    {
        // 真机形态（2026-10-17 终末地 1.6.4）：官方 delete_files.txt 点名 324 个旧 VFS 分块
        // （47.72 GiB），逐条删除后其专属分片目录应随之消失
        _downloader.Responses[ZipUrl] = ZipBytes;
        var stale = CreateStaleFile("Endfield_Data/StreamingAssets/VFS/AAAA/old.chk");
        var keep = CreateStaleFile("Endfield_Data/StreamingAssets/VFS/BBBB/keep.chk");

        await new PackageInstallerService(_downloader).InstallAsync(
            _tempDir.Path, Manifest(), obsoleteFiles: ["Endfield_Data/StreamingAssets/VFS/AAAA/old.chk"]);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(keep));
        Assert.False(Directory.Exists(_tempDir.FilePath("Endfield_Data", "StreamingAssets", "VFS", "AAAA"))); // 空目录回收
        Assert.True(Directory.Exists(_tempDir.FilePath("Endfield_Data", "StreamingAssets", "VFS", "BBBB")));
        Assert.Equal("MZ-stub", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe")));
    }

    [Fact]
    public async Task InstallAsync_BackslashObsoletePaths_StillDelete()
    {
        // 官方清单用 '/'，但清单不可信：Windows 风格分隔符同样归一后命中
        _downloader.Responses[ZipUrl] = ZipBytes;
        var stale = CreateStaleFile("a/b/old.chk");

        await new PackageInstallerService(_downloader).InstallAsync(
            _tempDir.Path, Manifest(), obsoleteFiles: [@"a\b\old.chk"]);

        Assert.False(File.Exists(stale));
    }

    [Fact]
    public async Task InstallAsync_ObsoleteFileAlsoProducedByArchive_Spared()
    {
        // 清单自相矛盾（点名的废弃文件恰是本次产物）时以产物为准：删掉刚解压的文件会让
        // 安装处于缺文件状态且无自愈路径（增量链 RF-C 同纪律）
        _downloader.Responses[ZipUrl] = ZipBytes;

        await new PackageInstallerService(_downloader).InstallAsync(
            _tempDir.Path, Manifest(), obsoleteFiles: ["game.exe"]);

        Assert.Equal("MZ-stub", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe")));
    }

    [Fact]
    public async Task InstallAsync_ObsoletePathEscapingSandbox_Skipped()
    {
        // 清单不可信：绝对路径与 ".." 穿越一律不删——受害者放在安装目录的上一级，任何一条
        // 被放行都会立刻改写它
        _downloader.Responses[ZipUrl] = ZipBytes;
        var outside = Path.GetFullPath(Path.Combine(_tempDir.Path, "..", "escape-victim.txt"));
        await File.WriteAllTextAsync(outside, "victim");
        try
        {
            await new PackageInstallerService(_downloader).InstallAsync(
                _tempDir.Path, Manifest(),
                obsoleteFiles: ["../escape-victim.txt", "./../escape-victim.txt", outside]);

            Assert.Equal("victim", await File.ReadAllTextAsync(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task InstallAsync_MissingObsoleteFile_Tolerated()
    {
        // 废弃文件本就不在盘上（用户手删/上次已清）：幂等，不抛错
        _downloader.Responses[ZipUrl] = ZipBytes;

        await new PackageInstallerService(_downloader).InstallAsync(
            _tempDir.Path, Manifest(), obsoleteFiles: ["gone/missing.chk"]);

        Assert.Equal("MZ-stub", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe")));
    }

    [Fact]
    public async Task InstallAsync_StateDirEntries_NotTouched()
    {
        // 清理只针对游戏文件：状态目录（.yagl）即使被清单点名也不得删除
        _downloader.Responses[ZipUrl] = ZipBytes;
        var stateFile = Path.Combine(_tempDir.Path, LocalStateService.StateDirName, "keep.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        await File.WriteAllTextAsync(stateFile, "{}");

        await new PackageInstallerService(_downloader).InstallAsync(
            _tempDir.Path, Manifest(), obsoleteFiles: [".yagl/keep.json"]);

        Assert.True(File.Exists(stateFile));
    }

    [Fact]
    public async Task InstallAsync_ObsoleteFileBehindSymlinkedDirectory_Skipped()
    {
        // F19 同族：既有目录符号链接会让删除穿透到沙箱之外——词法路径完全合法（无 ".."）
        // 也必须拒绝，否则清单点名 linked/victim 就能删掉链接目标里的文件
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("目录符号链接创建在 Windows 需特权，仅 Linux 确定性");
        }

        _downloader.Responses[ZipUrl] = ZipBytes;
        var outsideDir = _tempDir.FilePath("outside");
        Directory.CreateDirectory(outsideDir);
        var victim = Path.Combine(outsideDir, "victim.chk");
        await File.WriteAllTextAsync(victim, "victim");
        Directory.CreateSymbolicLink(_tempDir.FilePath("linked"), outsideDir);

        await new PackageInstallerService(_downloader).InstallAsync(
            _tempDir.Path, Manifest(), obsoleteFiles: ["linked/victim.chk"]);

        Assert.Equal("victim", await File.ReadAllTextAsync(victim));
        Assert.True(Directory.Exists(outsideDir)); // 链接目标目录本身也没被回收
    }

    [Fact]
    public async Task InstallAsync_ObsoleteFiles_ReportsCleaningUpPhase()
    {
        _downloader.Responses[ZipUrl] = ZipBytes;
        CreateStaleFile("stale/old.chk");
        var progress = new UpdateProgressCollector();

        await new PackageInstallerService(_downloader).InstallAsync(
            _tempDir.Path, Manifest(), progress, obsoleteFiles: ["stale/old.chk"]);

        Assert.Contains(progress.Frames, frame => frame.Phase == UpdatePhase.CleaningUp);
    }

    [Fact]
    public async Task ApplyPredownloadAsync_ObsoleteFiles_Deleted()
    {
        _downloader.Responses[ZipUrl] = ZipBytes;
        var stale = CreateStaleFile("Endfield_Data/VFS/old.chk");
        var service = new PackageInstallerService(_downloader);
        await service.PredownloadAsync(_tempDir.Path, Manifest());

        await service.ApplyPredownloadAsync(
            _tempDir.Path, Manifest(), obsoleteFiles: ["Endfield_Data/VFS/old.chk"]);

        Assert.False(File.Exists(stale));
        Assert.Equal("MZ-stub", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe")));
    }
}

[Collection("sequential")]
public class PackageInstallerMissingInfoTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();

    public void Dispose() => _tempDir.Dispose();

    private const string ZipUrl = "https://cdn.example.com/game-0.zip";

    [Fact]
    public async Task ApplyPredownload_WithMissingVerificationFields_UsesStagedArchiveWithoutRedownload()
    {
        // 回归（2026-09-20 复审）：包清单缺 size/md5（上游字段缺口 → 0/空串）时，暂存包
        // 不得被判损触发重下——否则每次应用都是数十 GB 全量重下（与下载器 Verify 同语义）
        var zip = TestZip.Create(("game.exe", "MZ-stub"));
        _downloader.Responses[ZipUrl] = zip;
        var manifest = new GameManifest
        {
            Version = "1.2.0",
            EntriesAreArchives = true,
            Files = [new ManifestFile("game-0.zip", 0, "", Url: ZipUrl)],
        };
        var service = new PackageInstallerService(_downloader);

        await service.PredownloadAsync(_tempDir.Path, manifest);
        var requestsAfterPredownload = _downloader.Requests.Count;

        await service.ApplyPredownloadAsync(_tempDir.Path, manifest);

        Assert.Equal(requestsAfterPredownload, _downloader.Requests.Count);
        Assert.Equal("MZ-stub", await File.ReadAllTextAsync(_tempDir.FilePath("game.exe")));
    }
}

[Collection("sequential")]
public class PackageInstallerCollisionTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public async Task Predownload_SameFileNameEntries_ThrowsInsteadOfSilentlyOverwriting()
    {
        // 回归（2026-09-20 三审）：暂存按清单路径末段落盘——两个条目同名（不同 URL 目录）时
        // 后包覆盖先包，解压循环把同一文件解两次、先包内容从未落地且全程无错误
        var zip1 = TestZip.Create(("a.txt", "1"));
        var zip2 = TestZip.Create(("b.txt", "2"));
        _downloader.Responses["https://cdn.example.com/v1/data.zip"] = zip1;
        _downloader.Responses["https://cdn.example.com/v2/data.zip"] = zip2;
        var manifest = new GameManifest
        {
            Version = "1.2.0",
            EntriesAreArchives = true,
            Files =
            [
                new ManifestFile("v1/data.zip", zip1.Length, Hashing.Md5Hex(zip1), Url: "https://cdn.example.com/v1/data.zip"),
                new ManifestFile("v2/data.zip", zip2.Length, Hashing.Md5Hex(zip2), Url: "https://cdn.example.com/v2/data.zip"),
            ],
        };

        await Assert.ThrowsAsync<YetAnotherGameLauncher.Core.Abstractions.UpdateException>(
            () => new PackageInstallerService(_downloader).PredownloadAsync(_tempDir.Path, manifest));
    }

    [Fact]
    public void ExtractArchive_EntryThroughExistingDirectorySymlink_RejectedInsteadOfWritingOut()
    {
        // F19（artifacts/bugs.md）：安装目录内已存在指向目录外的目录符号链接（游戏自带/搬盘手法）
        // 时，Directory.CreateDirectory 对既有链接目录是 no-op，ExtractToFile 经链接写出沙箱——
        // 词法防穿越（".."、盘符、rooted）管不住既有 reparse 点。威胁模型即代码自述"清单不可信"
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("目录符号链接创建在 Windows 需特权，仅 Linux 确定性");
        }

        var outside = _tempDir.FilePath("outside");
        Directory.CreateDirectory(outside);
        var installDir = _tempDir.FilePath("install");
        Directory.CreateDirectory(installDir);
        Directory.CreateSymbolicLink(Path.Combine(installDir, "mods"), outside);

        var zipPath = _tempDir.FilePath("pkg.zip");
        var zipBytes = TestZip.Create(("mods/evil.txt", "evil"));
        File.WriteAllBytes(zipPath, zipBytes);

        var ex = Record.Exception(() => PackageInstallerService.ExtractArchive(zipPath, installDir, "pkg.zip"));

        Assert.NotNull(ex); // 红落此断言：当前静默写出
        Assert.False(File.Exists(Path.Combine(outside, "evil.txt")), "沙箱外不得出现解压产物");
    }

    [Fact]
    public void PruneForeignPackages_CaseVariantStaleFile_PrunedOnLinux()
    {
        // F20：keep 集合恒 OrdinalIgnoreCase 与 seenTargets（Windows 才 IgnoreCase）策略不一致——
        // Linux 上两代清单间包名仅大小写变化时旧代残留永不清理（磁盘滞留，数十 GB 级）
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("平台比较器语义差异：本用例钉 Linux=Ordinal 分支");
        }

        var packagesDir = _tempDir.FilePath("pkgs");
        Directory.CreateDirectory(packagesDir);
        var stale = Path.Combine(packagesDir, "pkg.zip");
        File.WriteAllText(stale, "old-generation");
        var manifest = new GameManifest
        {
            Version = "2.0.0",
            EntriesAreArchives = true,
            Files = [new ManifestFile("PKG.ZIP", 20, "", Url: "https://cdn.example.com/PKG.ZIP")],
        };

        PackageInstallerService.PruneForeignPackages(packagesDir, manifest);

        Assert.False(File.Exists(stale)); // 红落此断言：当前 IgnoreCase keep 把它当保留
    }

    [Fact]
    public void ExtractArchive_ExistingFileSymlinkAtTarget_RejectedInsteadOfWritingThrough()
    {
        // 复审 R2（F19 修复的残留面审查，/tmp 探针实锤）：目录符号链接防住后，目标处既有的
        // 文件符号链接仍会写穿——ExtractToFile(overwrite:true) 经链接改写沙箱外的真实文件
        //（Unix open(O_CREAT) 与 Windows CreateFile 都跟随符号链接）
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("文件符号链接创建在 Windows 需特权，仅 Linux 确定性");
        }

        var outside = _tempDir.FilePath("outside-victim.txt");
        File.WriteAllText(outside, "victim-original");
        var installDir = _tempDir.FilePath("install");
        Directory.CreateDirectory(Path.Combine(installDir, "mods"));
        File.CreateSymbolicLink(Path.Combine(installDir, "mods", "evil.txt"), outside);

        var zipPath = _tempDir.FilePath("pkg.zip");
        File.WriteAllBytes(zipPath, TestZip.Create(("mods/evil.txt", "EVIL-VIA-LINK")));

        var ex = Record.Exception(() => PackageInstallerService.ExtractArchive(zipPath, installDir, "pkg.zip"));

        Assert.NotNull(ex); // 红落此断言：当前静默写穿
        Assert.Equal("victim-original", File.ReadAllText(outside)); // 沙箱外文件不得被改写
    }

    [Fact]
    public void ExtractArchive_ExistingHardLinkAtTarget_RejectedInsteadOfWritingThrough()
    {
        // F43（artifacts/bugs.md）：硬链接没有 ReparsePoint 标记，File.GetAttributes 返回的
        // 是共享 inode 的属性——目录/文件符号链接防线对它全部失效，ExtractToFile(overwrite:true)
        // 沿既有 inode 写会截断改写同卷沙箱外真实文件。link(2)/CreateHardLinkW 双平台均无需
        // 特权；victim 与 install 同在 TempDir 下保证同卷。
        // 架构前提（AGENTS.md 复审纪律 2）：非 x64 Linux 的 stat 布局未实现、生产 fail-open
        // 返 1——夹具可建硬链接但防线不触发，不 Skip 即假红
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            || (OperatingSystem.IsLinux()
                && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                    != System.Runtime.InteropServices.Architecture.X64))
        {
            Assert.Skip("硬链接夹具 + x64 stat(2) 探测：仅 Linux x64 / Windows 腿确定性");
        }

        var outside = _tempDir.FilePath("outside-victim.txt");
        File.WriteAllText(outside, "victim-original");
        var installDir = _tempDir.FilePath("install");
        Directory.CreateDirectory(Path.Combine(installDir, "mods"));
        MakeHardLink(Path.Combine(installDir, "mods", "evil.txt"), outside);

        var zipPath = _tempDir.FilePath("pkg.zip");
        File.WriteAllBytes(zipPath, TestZip.Create(("mods/evil.txt", "EVIL-VIA-HARDLINK")));

        var ex = Record.Exception(() => PackageInstallerService.ExtractArchive(zipPath, installDir, "pkg.zip"));

        // 断言钉异常类型而非"有异常"：glibc<2.33 上 stat 探测不可用（fail-open 放行）时
        // 不抛任何异常——NotNull 形态会假绿，UpdateException 类型断言使其转红可见
        Assert.IsType<UpdateException>(ex);
        Assert.Equal("victim-original", File.ReadAllText(outside)); // 共享 inode 的沙箱外文件不得被改写
    }

    [Fact]
    public void ExtractArchive_DeclaredTotalOverCap_ThrowsBeforeExtractingRemainingEntries()
    {
        // 加固（artifacts/bugs.md）：清单不可信威胁模型下解压无体量上限，zip bomb 可写满磁盘。
        // 累计声明尺寸（entry.Length）达阈值即拒；测试缝注入小阈值。红证据：超限后剩余条目未落盘
        var zipPath = _tempDir.FilePath("bomb.zip");
        File.WriteAllBytes(zipPath, TestZip.Create(
            ("data/f0.bin", new string('0', 4096)),
            ("data/f1.bin", new string('0', 4096)),
            ("data/f2.bin", new string('0', 4096)),
            ("data/f3.bin", new string('0', 4096)),
            ("data/f4.bin", new string('0', 4096)),
            ("data/f5.bin", new string('0', 4096)),
            ("data/f6.bin", new string('0', 4096)),
            ("data/f7.bin", new string('0', 4096))));
        var installDir = _tempDir.FilePath("install");
        Directory.CreateDirectory(installDir);

        var ex = Record.Exception(() =>
            PackageInstallerService.ExtractArchive(zipPath, installDir, "bomb.zip", maxExtractBytes: 8 * 1024));

        Assert.NotNull(ex); // 红落此断言：当前无上限、全量解压
        Assert.True(File.Exists(Path.Combine(installDir, "data", "f1.bin"))); // 阈值内条目正常落盘
        Assert.False(File.Exists(Path.Combine(installDir, "data", "f7.bin"))); // 超限后不再解压
    }

    [Fact]
    public void IsArchiveIntact_ArchiveRemovedBetweenCheckAndProbe_ReportsNotIntact()
    {
        // 次级 suspect（第 13 轮，artifacts/bugs.md）：IsArchiveIntact 的 Exists→Length TOCTOU
        //（F18 同型）：暂存包被并发删除/手删时裸 FNFE 穿出 ApplyPredownloadAsync 折算成 Unknown。
        // 按"不完整"报告走重下（确定性缝注入，F18 缝同款）
        var archivePath = _tempDir.FilePath("pkg.zip");
        var zipBytes = TestZip.Create(("a.txt", "1"));
        File.WriteAllBytes(archivePath, zipBytes);
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            EntriesAreArchives = true,
            Files = [new ManifestFile("pkg.zip", zipBytes.Length, "")], // size>0 → Length 探测段可达
        };
        PackageInstallerService.StagedArchiveRemovedBetweenCheckAndProbeForTests = File.Delete;
        try
        {
            Assert.False(PackageInstallerService.IsArchiveIntact(archivePath, manifest.Files[0])); // 红落此断言：当前 FNFE 穿出
        }
        finally
        {
            PackageInstallerService.StagedArchiveRemovedBetweenCheckAndProbeForTests = null;
        }
    }

    [Fact]
    public void DeriveMaxExtractBytes_FloorForSmallArchives_RatioForLargeOnes()
    {
        // 默认阈值双腿：小压缩包吃 1GiB 绝对下限（合法补丁包永不被误杀），大压缩包吃
        // 100 倍比例（合法包内容已压缩、比例≈1-3，炸弹比例成千）
        Assert.Equal(1L << 30, PackageInstallerService.DeriveMaxExtractBytes(1024));
        Assert.Equal(100L * (1L << 30), PackageInstallerService.DeriveMaxExtractBytes(1L << 30));
    }

    /// <summary>创建硬链接（F43 测试夹具）：Windows 走 CreateHardLinkW，其余走 link(2)。</summary>
    private static void MakeHardLink(string linkPath, string existingPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLink(linkPath, existingPath, IntPtr.Zero))
            {
                throw new IOException($"CreateHardLinkW failed, win32 error {Marshal.GetLastWin32Error()}");
            }
        }
        else if (link(existingPath, linkPath) != 0)
        {
            throw new IOException($"link(2) failed, errno {Marshal.GetLastWin32Error()}");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int link(string oldpath, string newpath);
}
