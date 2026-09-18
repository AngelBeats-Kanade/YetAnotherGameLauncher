using Xunit;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>
/// 本地状态文件读取容错（async review 修复回归，2026-09-19）：
/// Load 处于 RefreshAsync 的 fire-and-forget 调用链上，损坏/被占用一律视为未安装，
/// 任何异常不得穿出（原实现 IOException 会逃逸成未观察任务异常）。
/// </summary>
public sealed class LocalStateServiceTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private LocalStateService CreateService() => new(_temp.Path);

    private string StatePath => Path.Combine(_temp.Path, ".yagl", "state.json");

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var service = CreateService();
        await service.SaveAsync(new LocalGameState { GameId = "g", ServerId = "s", Version = "1.0.0" });

        var state = service.Load("g", "s");

        Assert.NotNull(state);
        Assert.Equal("1.0.0", state.Version);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(CreateService().Load("g", "s"));
    }

    [Fact]
    public async Task Load_CorruptedJson_ReturnsNull()
    {
        var service = CreateService();
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, "{ not json");

        Assert.Null(service.Load("g", "s"));
    }

    [Fact]
    public async Task Load_LockedFile_ReturnsNull()
    {
        // FileShare.None 独占 = 杀软/编辑器占用语义：IOException 必须被吞成 null
        var service = CreateService();
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, "{}");
        using var lockHandle = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Null(service.Load("g", "s"));
    }

    [Fact]
    public async Task Load_GameOrServerMismatch_ReturnsNull()
    {
        var service = CreateService();
        await service.SaveAsync(new LocalGameState { GameId = "g", ServerId = "s", Version = "1.0.0" });

        Assert.Null(service.Load("other", "s"));
        Assert.Null(service.Load("g", "other"));
    }
}
