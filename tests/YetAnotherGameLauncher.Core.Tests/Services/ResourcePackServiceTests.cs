using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>
/// 档位资源包的本地面（2026-10-19）：度量、清理预览与显式清理其它档位——
/// 只删各档位清单点名且解析后位于该档位目录之下的文件（含下载半成品 .temp）；
/// 逐文件容错；选中档位与游离文件都不碰。
/// </summary>
[Collection("sequential")]
public class ResourcePackServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeChannel _channel = new();
    private readonly GameServer _server = new() { Id = "cn", Name = "国服" };

    public void Dispose() => _tempDir.Dispose();

    private static ResourcePackManifest Pack(string tier, params (string Path, long Size)[] files) =>
        new(tier, $"Client/Content/{tier.ToUpperInvariant()}", "3.7.1",
            [.. files.Select(f => new ManifestFile(f.Path, f.Size, "", Url: null))]);

    private void Write(string relativePath, string content)
    {
        var path = _tempDir.FilePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ---------- 度量 ----------

    [Fact]
    public void Measure_CountsOnlyFilesWithMatchingSize()
    {
        var pack = Pack("uhd",
            ("Client/Content/UHD/pak1.pak", 5),
            ("Client/Content/UHD/pak2.pak", 5),
            ("Client/Content/UHD/pak3.pak", 5));
        Write("Client/Content/UHD/pak1.pak", "12345");   // 尺寸一致 → 计入
        Write("Client/Content/UHD/pak2.pak", "123");     // 尺寸不符 → 不计
        // pak3 缺失

        var usage = ResourcePackService.Measure(_tempDir.Path, pack);

        Assert.Equal("uhd", usage.Tier);
        Assert.Equal("Client/Content/UHD", usage.PackDirectory);
        Assert.Equal(1, usage.PresentFiles);
        Assert.Equal(3, usage.TotalFiles);
        Assert.Equal(5, usage.PresentBytes);
        Assert.Equal(15, usage.TotalBytes);
    }

    // ---------- 预览 ----------

    [Fact]
    public async Task PreviewCleanup_ReportsOtherTierUsageAndKeptTierPresence()
    {
        _channel.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));
        _channel.ResourcePacks["sd"] = Pack("sd", ("Client/Content/SD/pak.pak", 6));
        _channel.ResourcePacks["uhd"] = Pack("uhd", ("Client/Content/UHD/pak.pak", 7));
        Write("Client/Content/HD/pak.pak", "12345");
        Write("Client/Content/UHD/pak.pak", "1234567");

        var preview = await new ResourcePackService(_channel).PreviewCleanupAsync(_tempDir.Path, _server, "uhd");

        Assert.Equal("uhd", preview.KeptTier);
        Assert.True(preview.KeptTierPresent);
        Assert.Equal(["hd", "sd"], preview.Targets.Select(t => t.Tier));
        Assert.Equal(5, preview.ReclaimableBytes);
        Assert.True(preview.KeptTierComplete);
        Assert.Empty(preview.UnavailableTiers);
    }

    [Fact]
    public async Task PreviewCleanup_KeptTierNotOnDisk_Flagged()
    {
        _channel.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));
        Write("Client/Content/HD/pak.pak", "12345");

        var preview = await new ResourcePackService(_channel).PreviewCleanupAsync(_tempDir.Path, _server, "uhd");

        Assert.False(preview.KeptTierPresent);
    }

    [Fact]
    public async Task PreviewCleanup_MissingPackManifest_ListsUnavailableTier()
    {
        _channel.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));

        var preview = await new ResourcePackService(_channel).PreviewCleanupAsync(_tempDir.Path, _server, null);

        // 未设置档位 = hd 生效：清理目标 sd/uhd；两者的包清单都取不到（渠道没有）→ 如实列举
        Assert.Equal("hd", preview.KeptTier);
        Assert.True(preview.KeptTierKnown); // hd 包清单取到了（只是目标档位取不到）
        Assert.Empty(preview.Targets);
        Assert.Equal(["sd", "uhd"], preview.UnavailableTiers);
    }

    // ---------- 清理 ----------

    [Fact]
    public async Task Cleanup_DeletesOtherTierFilesAndKeepsSelectedTier()
    {
        _channel.ResourcePacks["hd"] = Pack("hd",
            ("Client/Content/HD/pak1.pak", 5), ("Client/Content/HD/pak2.pak", 5));
        _channel.ResourcePacks["sd"] = Pack("sd", ("Client/Content/SD/pak.pak", 3));
        Write("Client/Content/HD/pak1.pak", "12345");
        Write("Client/Content/HD/pak2.pak", "12345");
        Write("Client/Content/SD/pak.pak", "123");
        Write("Client/Content/UHD/pak.pak", "uhd"); // 不在任何清单里的游离文件：本服务不碰
        Write("Client/Content/HD/stray.txt", "stray"); // 档位目录内的游离文件：同样不碰（清单点名才删）

        var result = await new ResourcePackService(_channel).CleanupOtherTiersAsync(_tempDir.Path, _server, "uhd");

        Assert.Equal("uhd", result.KeptTier);
        Assert.Equal(3, result.RemovedFiles);
        Assert.Equal(13, result.FreedBytes);
        Assert.Equal(0, result.FailedFiles);
        Assert.False(File.Exists(_tempDir.FilePath("Client/Content/HD/pak1.pak")));
        Assert.False(File.Exists(_tempDir.FilePath("Client/Content/SD/pak.pak")));
        Assert.True(File.Exists(_tempDir.FilePath("Client/Content/UHD/pak.pak")));
        Assert.True(File.Exists(_tempDir.FilePath("Client/Content/HD/stray.txt")));
    }

    [Fact]
    public async Task Cleanup_RemovesEmptiedDirectories()
    {
        _channel.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));
        Write("Client/Content/HD/pak.pak", "12345");

        await new ResourcePackService(_channel).CleanupOtherTiersAsync(_tempDir.Path, _server, "uhd");

        Assert.False(Directory.Exists(_tempDir.FilePath("Client/Content/HD")));
    }

    [Fact]
    public async Task Cleanup_MissingFiles_AreNotFailures()
    {
        _channel.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));

        var result = await new ResourcePackService(_channel).CleanupOtherTiersAsync(_tempDir.Path, _server, "uhd");

        Assert.Equal(0, result.RemovedFiles);
        Assert.Equal(0, result.FailedFiles);
    }

    [Fact]
    public async Task Cleanup_ManifestFetchFails_ListsUnavailableAndKeepsGoing()
    {
        _channel.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));
        Write("Client/Content/HD/pak.pak", "12345");
        _channel.ResourcePackError = new UpdateException("boom");

        var result = await new ResourcePackService(_channel).CleanupOtherTiersAsync(_tempDir.Path, _server, "uhd");

        Assert.Equal(["hd", "sd"], result.UnavailableTiers.OrderBy(t => t));
        Assert.Equal(0, result.RemovedFiles);
        Assert.True(File.Exists(_tempDir.FilePath("Client/Content/HD/pak.pak")));
    }

    [Fact]
    public async Task Cleanup_ManifestEntryOutsidePackDirectory_IsSkipped()
    {
        // 清单形态漂移（条目指向档位目录之外）：绝不删——与同步清理"绝不越出安装目录"同纪律
        _channel.ResourcePacks["hd"] = Pack("hd",
            ("Client/Content/HD/pak.pak", 5),
            ("Client/Content/Paks/victim.pak", 5));
        Write("Client/Content/HD/pak.pak", "12345");
        Write("Client/Content/Paks/victim.pak", "12345");

        var result = await new ResourcePackService(_channel).CleanupOtherTiersAsync(_tempDir.Path, _server, "uhd");

        Assert.Equal(1, result.RemovedFiles);
        Assert.True(File.Exists(_tempDir.FilePath("Client/Content/Paks/victim.pak")));
    }

    [Fact]
    public void Measure_CountsTempLeftoversOfEntries()
    {
        // 半成品 `.temp`（下载器命名约定）计入"可释放"：保留目录下的残件同步收尾不碰（2026-10-19 review）
        var pack = Pack("hd",
            ("Client/Content/HD/pak1.pak", 5),
            ("Client/Content/HD/pak2.pak", 5));
        Write("Client/Content/HD/pak1.pak", "12345");
        Write("Client/Content/HD/pak1.pak.temp", "123"); // 半成品：尺寸与清单无关，按实计
        Write("Client/Content/HD/pak2.pak.temp", "12");

        var usage = ResourcePackService.Measure(_tempDir.Path, pack);

        Assert.Equal(1, usage.PresentFiles);
        Assert.Equal(2, usage.TempFiles);
        Assert.Equal(5, usage.TempBytes);
        Assert.Equal(10, usage.ReclaimableBytes);
        Assert.False(usage.Complete);
    }

    [Fact]
    public async Task PreviewCleanup_KeptTierPartiallyPresent_NotComplete()
    {
        // 选中档位下到一半：有文件但不完整——清理会删掉可能唯一完整的其它档，必须给警示（2026-10-19 review）
        _channel.ResourcePacks["uhd"] = Pack("uhd",
            ("Client/Content/UHD/pak1.pak", 5),
            ("Client/Content/UHD/pak2.pak", 5));
        _channel.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));
        Write("Client/Content/UHD/pak1.pak", "12345"); // pak2 缺失 → 不完整
        Write("Client/Content/HD/pak.pak", "12345");

        var preview = await new ResourcePackService(_channel).PreviewCleanupAsync(_tempDir.Path, _server, "uhd");

        Assert.True(preview.KeptTierKnown);
        Assert.True(preview.KeptTierPresent);
        Assert.False(preview.KeptTierComplete);
    }

    [Fact]
    public async Task PreviewCleanup_ManifestFetchFails_ListsUnavailableInsteadOfThrowing()
    {
        // 单档清单取不到不炸整页：与清理链"尽力而为"同契约（2026-10-19 review）
        _channel.ResourcePacks["uhd"] = Pack("uhd", ("Client/Content/UHD/pak.pak", 5));
        Write("Client/Content/UHD/pak.pak", "12345");
        _channel.ResourcePackError = new UpdateException("boom");

        var preview = await new ResourcePackService(_channel).PreviewCleanupAsync(_tempDir.Path, _server, "uhd");

        Assert.False(preview.KeptTierKnown); // 选中档位清单也取不到：落盘状态未知，不得断言"未下载"
        Assert.Equal(["hd", "sd", "uhd"], preview.UnavailableTiers.OrderBy(t => t)); // 含选中档位：如实暴露未知
    }

    [Fact]
    public async Task Cleanup_RemovesTempLeftoversOfOtherTier()
    {
        // 中止的其它档位下载只留 .temp 残件时同样要被清掉（此前预览说"没得清"、清理也不删）
        _channel.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));
        Write("Client/Content/HD/pak.pak.temp", "123");

        var result = await new ResourcePackService(_channel).CleanupOtherTiersAsync(_tempDir.Path, _server, "uhd");

        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(3, result.FreedBytes);
        Assert.False(File.Exists(_tempDir.FilePath("Client/Content/HD/pak.pak.temp")));
    }

    [Fact]
    public async Task Cleanup_ManifestEntryWithParentSegment_IsSkipped()
    {
        // `..` 段骗得过词法前缀却解析到保留档：解析后必须确实位于该档位目录之内（2026-10-19 review 纵深修复）
        _channel.ResourcePacks["hd"] = Pack("hd",
            ("Client/Content/HD/pak.pak", 5),
            ("Client/Content/HD/../SD/victim.pak", 5));
        Write("Client/Content/HD/pak.pak", "12345");
        Write("Client/Content/SD/victim.pak", "12345");

        var result = await new ResourcePackService(_channel).CleanupOtherTiersAsync(_tempDir.Path, _server, "uhd");

        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.True(File.Exists(_tempDir.FilePath("Client/Content/SD/victim.pak")), "解析后落在保留档目录内的条目不得删除");
    }

    [Fact]
    public async Task Cleanup_KeptTierIsEffectiveTier_UnsetKeepsHd()
    {
        _channel.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));
        _channel.ResourcePacks["uhd"] = Pack("uhd", ("Client/Content/UHD/pak.pak", 5));
        Write("Client/Content/HD/pak.pak", "12345");
        Write("Client/Content/UHD/pak.pak", "12345");

        var result = await new ResourcePackService(_channel).CleanupOtherTiersAsync(_tempDir.Path, _server, null);

        Assert.Equal("hd", result.KeptTier);
        Assert.True(File.Exists(_tempDir.FilePath("Client/Content/HD/pak.pak")));
        Assert.False(File.Exists(_tempDir.FilePath("Client/Content/UHD/pak.pak")));
    }
}
