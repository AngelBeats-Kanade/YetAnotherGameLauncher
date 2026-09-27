using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>
/// 捕获日志记录的内存 <see cref="ILogger"/>（测试缝）：按序记录每条 (级别, 异常, 格式化消息)，
/// 供断言"失败必须落日志"类守卫（F49）。IsEnabled 恒 true，无作用域。
/// </summary>
public sealed class CapturingLogger : ILogger
{
    /// <summary>按到达顺序记录的全部条目。</summary>
    public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

    /// <summary>是否存在消息含指定子串的指定级别记录。</summary>
    public bool Has(LogLevel level, string messageSubstring) => Entries.Any(e =>
        e.Level == level && e.Message.Contains(messageSubstring, StringComparison.Ordinal));

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, exception, formatter(state, exception)));
}
