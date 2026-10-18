using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 应用级日志文件（2026-10-17 立案④：此前只有游戏启动日志，更新类事故只能靠文件 mtime 复盘）。
/// 形态：<c>{directory}/yagl-yyyyMMdd.log</c> 一天一个、追加写入（重启/多实例不截断），
/// 构造时清理保留期外的同类文件（只碰 <c>yagl-*.log</c>，不动同在 logs/ 的游戏启动日志）。
/// 定位：诊断尾巴，不是审计——任何写失败（占用/权限/磁盘满）一律静默吞掉，绝不影响业务；
/// 级别过滤交给 logging 管线的全局最低级别。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>文件名前缀（保留期清理只针对本前缀）。</summary>
    public const string FilePrefix = "yagl";

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;

    /// <param name="directory">日志目录（不存在则创建；创建失败一律吞掉，写日志时再按失败处理）。</param>
    /// <param name="retentionDays">保留天数（按文件名里的日期判定，缺省 14 天）。</param>
    public FileLoggerProvider(string directory, int retentionDays = 14)
    {
        _directory = directory;
        _retentionDays = retentionDays;
        try
        {
            Directory.CreateDirectory(directory);
            PruneExpired(DateOnly.FromDateTime(DateTime.Today));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志目录不可用：后续写入走同款吞异常路径
        }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>写入一行（时间戳 + 级别 + 分类 + 消息，异常详情另起行）；失败静默。</summary>
    internal void Write(DateTime timestamp, LogLevel level, string category, string message)
    {
        lock (_gate)
        {
            try
            {
                var writer = EnsureWriter(DateOnly.FromDateTime(timestamp));
                writer.WriteLine($"{timestamp:yyyy-MM-dd HH:mm:ss.fff} [{LevelTag(level)}] {category}: {message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // 占用/无权限/磁盘满：日志不得影响业务
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>取当日文件写入器（跨天换文件；打开失败由调用方吞掉）。</summary>
    private StreamWriter EnsureWriter(DateOnly date)
    {
        if (_writer is not null && _writerDate == date)
        {
            return _writer;
        }

        _writer?.Dispose();
        var path = Path.Combine(_directory, $"{FilePrefix}-{date:yyyyMMdd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream) { AutoFlush = true }; // 崩溃/被杀也保住最后几行（事故复盘要的就是它）
        _writerDate = date;
        return _writer;
    }

    /// <summary>删除保留期外的 <c>yagl-yyyyMMdd.log</c>（文件名日期解析不出的不碰）。</summary>
    private void PruneExpired(DateOnly today)
    {
        var cutoff = today.AddDays(-_retentionDays);
        foreach (var path in Directory.EnumerateFiles(_directory, $"{FilePrefix}-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var stamp = name[(FilePrefix.Length + 1)..];
            if (DateOnly.TryParseExact(stamp, "yyyyMMdd", out var date) && date < cutoff)
            {
                FileUtilities.DeleteQuiet(path);
            }
        }
    }

    /// <summary>级别标签（对齐常见日志形态；保持短、可 grep）。</summary>
    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Info",
        LogLevel.Warning => "Warn",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Fatal",
        _ => level.ToString(),
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None)
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += Environment.NewLine + exception;
            }

            provider.Write(DateTime.Now, logLevel, category, message);
        }
    }
}
