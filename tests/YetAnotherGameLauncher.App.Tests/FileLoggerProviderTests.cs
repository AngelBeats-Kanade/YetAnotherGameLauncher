using Microsoft.Extensions.Logging;
using Xunit;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 应用级日志文件（2026-10-17 立案④：此前只有游戏启动日志，更新类事故只能靠文件 mtime 复盘）：
/// 按天一个文件、保留期清理、写失败绝不影响业务。
/// </summary>
public class FileLoggerProviderTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Log_WritesLevelCategoryAndFormattedMessage()
    {
        using var provider = new FileLoggerProvider(_temp.Path);
        var logger = provider.CreateLogger("Test.Category");

        logger.LogInformation("update finished {From} -> {To}", "1.5.3", "1.6.4");
        logger.LogWarning("payload kept");
        provider.Dispose();

        var file = Assert.Single(Directory.GetFiles(_temp.Path, "yagl-*.log"));
        // 超时提示用同一个 helper 推导应用日志路径；这里钉住 provider 实际写盘位置。
        Assert.Equal(
            FileUtilities.ApplicationLogFilePath(
                _temp.Path, DateOnly.FromDateTime(DateTime.Now)),
            file);
        var text = File.ReadAllText(file);
        Assert.Contains("[Info]", text);
        Assert.Contains("[Warn]", text);
        Assert.Contains("Test.Category", text);
        Assert.Contains("update finished 1.5.3 -> 1.6.4", text); // 结构化参数已展开
        Assert.Contains("payload kept", text);
    }

    [Fact]
    public void Log_WithException_IncludesExceptionDetail()
    {
        using var provider = new FileLoggerProvider(_temp.Path);
        var logger = provider.CreateLogger("Test.Exception");

        logger.LogError(new InvalidOperationException("boom-detail"), "update failed");
        provider.Dispose();

        var text = File.ReadAllText(Assert.Single(Directory.GetFiles(_temp.Path, "yagl-*.log")));
        Assert.Contains("update failed", text);
        Assert.Contains("boom-detail", text); // 异常消息（含类型名）随行落盘
        Assert.Contains(nameof(InvalidOperationException), text);
    }

    [Fact]
    public void Log_NoneLevel_IsIgnored()
    {
        using var provider = new FileLoggerProvider(_temp.Path);
        var logger = provider.CreateLogger("Test.None");

        Assert.False(logger.IsEnabled(LogLevel.None));
        logger.Log(LogLevel.None, new EventId(0), "state", null, (s, _) => s);
        provider.Dispose();

        Assert.Empty(Directory.GetFiles(_temp.Path, "yagl-*.log"));
    }

    [Fact]
    public void Constructor_PrunesExpiredDailyFiles_AndLeavesEverythingElse()
    {
        var today = DateTime.Today;
        var expired = Path.Combine(_temp.Path, $"yagl-{today.AddDays(-30):yyyyMMdd}.log");
        var current = Path.Combine(_temp.Path, $"yagl-{today:yyyyMMdd}.log");
        var unrelated = Path.Combine(_temp.Path, "launch-wingame-20260101-0000-abcd.log");
        File.WriteAllText(expired, "old");
        File.WriteAllText(current, "new");
        File.WriteAllText(unrelated, "keep");

        using var provider = new FileLoggerProvider(_temp.Path, retentionDays: 14);

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(unrelated)); // 不碰非本前缀文件（游戏启动日志）
    }

    [Fact]
    public void Log_AppendsAcrossProviderRestarts()
    {
        // 同日重开（应用重启/多实例）只追加不清空
        using (var first = new FileLoggerProvider(_temp.Path))
        {
            first.CreateLogger("c").LogInformation("first-run");
        }

        using (var second = new FileLoggerProvider(_temp.Path))
        {
            second.CreateLogger("c").LogInformation("second-run");
        }

        var text = File.ReadAllText(Assert.Single(Directory.GetFiles(_temp.Path, "yagl-*.log")));
        Assert.Contains("first-run", text);
        Assert.Contains("second-run", text);
    }

    [Fact]
    public void Log_UnwritableTarget_DoesNotThrow()
    {
        // 目标被同名文件占位：目录建不出来，写日志必须静默吞掉（日志不得成为新的故障源）
        var blocked = Path.Combine(_temp.Path, "blocked");
        File.WriteAllText(blocked, "not a directory");
        using var provider = new FileLoggerProvider(blocked);

        provider.CreateLogger("c").LogInformation("still safe");
    }

    [Fact]
    public void IsEnabled_AnyRealLevel_IsTrue()
    {
        // 级别过滤交给 logging 管线（全局最低级别），provider 自身不额外丢内容
        using var provider = new FileLoggerProvider(_temp.Path);
        var logger = provider.CreateLogger("c");

        Assert.True(logger.IsEnabled(LogLevel.Trace));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
        Assert.Null(logger.BeginScope("scope"));
    }
}
