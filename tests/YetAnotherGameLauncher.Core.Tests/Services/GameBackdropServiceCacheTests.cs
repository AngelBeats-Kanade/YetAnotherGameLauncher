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
}
