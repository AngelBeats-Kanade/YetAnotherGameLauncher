using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class GameBackdropServiceCacheTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public async Task ResolveCachedAsync_UnreadableMeta_ReturnsNullInsteadOfThrowing()
    {
        // 次级 suspect（第 9 轮，artifacts/bugs.md）：ReadMeta 的异常过滤缺 UAE——meta.json 被
        // 拒读（chmod 000/ACL）时裸 UnauthorizedAccessException 穿出 RefreshAsync 的弃元调用点
        // 静默失败。按"无缓存"处理走重新获取。前提自检：root/CAP_DAC_OVERRIDE 无视权限位
        //（正向守卫 + else Skip：CA1416 平台分析器只认 OperatingSystem.IsLinux() 直接分支）
        if (OperatingSystem.IsLinux())
        {
            var metaPath = Path.Combine(_tempDir.Path, "g1", "meta.json");
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
            File.WriteAllText(metaPath, "{\"region\":\"global\"}");
            File.SetUnixFileMode(metaPath, UnixFileMode.None);
            try
            {
                _ = File.ReadAllText(metaPath);
                Assert.Skip("当前进程可无视权限位读文件（root/CAP_DAC_OVERRIDE），拒读形态不成立");
            }
            catch (UnauthorizedAccessException)
            {
                // 前提成立：读确实被拒
            }

            var service = new GameBackdropService(
                new HttpClient(new StubHttpHandler()),
                new Dictionary<string, IBackdropResolver>(),
                cacheRoot: _tempDir.Path);
            var request = new BackdropRequest("g1", "kuro", "global", null, new Dictionary<string, string>());

            var ex = await Record.ExceptionAsync(() => service.ResolveCachedAsync(request));

            Assert.Null(ex); // 红落此断言：当前 UAE 穿出
            Assert.Null(await service.ResolveCachedAsync(request)); // 拒读按无缓存处理
        }
        else
        {
            Assert.Skip("chmod 权限位拒读语义仅 Linux 确定性");
        }
    }

    [Fact]
    public async Task ResolveCachedAsync_MetaMissingFileField_ReturnsNullInsteadOfThrowing()
    {
        // review 立案（2026-09-26 第 2 轮，artifacts/bugs.md）：可读但缺 file 字段的 meta.json
        //（手改/格式演化才可达）在 IsCacheFreshFor 的 Path.Combine(cacheDir, null) 裸
        // ArgumentNullException 穿出——按无缓存处理（与拒读/损坏同语义族）
        var metaPath = Path.Combine(_tempDir.Path, "g1", "meta.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
        File.WriteAllText(metaPath, "{\"region\":\"global\"}");

        var service = new GameBackdropService(
            new HttpClient(new StubHttpHandler()),
            new Dictionary<string, IBackdropResolver>(),
            cacheRoot: _tempDir.Path);
        var request = new BackdropRequest("g1", "kuro", "global", null, new Dictionary<string, string>());

        var ex = await Record.ExceptionAsync(() => service.ResolveCachedAsync(request));

        Assert.Null(ex); // 红落此断言：无判空时裸 ANE 穿出
        Assert.Null(await service.ResolveCachedAsync(request));
    }

    [Fact]
    public async Task ResolveAsync_OfflineFallback_MetaMissingFileField_ReturnsNullInsteadOfThrowing()
    {
        // review 第 3 轮 F1（artifacts/bugs.md）：IsCacheFreshFor 判空只封了 fresh-hit 入口——
        // ResolveCoreAsync 的缓存兜底（离线/重取失败）直接 ResolvedFromCache(cachedHit)，
        // 缺 file 字段的 meta 在此同样裸 ANE。判空下沉 ResolvedFromCache 后三入口全封
        var metaPath = Path.Combine(_tempDir.Path, "g1", "meta.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
        File.WriteAllText(metaPath, "{\"region\":\"global\",\"url\":\"https://cdn.example/bg.png\",\"kind\":\"Image\"}");

        var service = new GameBackdropService(
            new HttpClient(new StubHttpHandler()),
            new Dictionary<string, IBackdropResolver> { ["kuro"] = new NullResolver() },
            cacheRoot: _tempDir.Path);
        var request = new BackdropRequest("g1", "kuro", "global", null, new Dictionary<string, string>());

        var ex = await Record.ExceptionAsync(() => service.ResolveAsync(request));

        Assert.Null(ex); // 红落此断言：缓存兜底路径无判空时裸 ANE 穿出
        Assert.Null(await service.ResolveAsync(request));
    }

    /// <summary>恒 null 解析器（离线形态：解析器在册但暂无法确定背景，触发缓存兜底路径）。</summary>
    private sealed class NullResolver : IBackdropResolver
    {
        public Task<BackdropSource?> GetBackdropUrlAsync(
            BackdropRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<BackdropSource?>(null);
    }
}
