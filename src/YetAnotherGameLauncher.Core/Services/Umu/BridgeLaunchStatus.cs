using System.Diagnostics;
using System.Globalization;

namespace YetAnotherGameLauncher.Core.Services.Umu;

/// <summary>诱饵父进程桥启动状态的种类。</summary>
public enum BridgeLaunchState
{
    /// <summary>helper 已成功创建游戏进程（游戏此后退出码不代表桥失败）。</summary>
    Ready,

    /// <summary>helper 在创建游戏前失败。</summary>
    Failed,

    /// <summary>等待超时：helper 未写出 READY/ERROR（可能仍在启动，也可能崩溃）。</summary>
    TimedOut,
}

/// <summary>一次桥启动状态读取结果。</summary>
public readonly record struct BridgeLaunchResult(
    BridgeLaunchState State, int? ExitCode = null, string? Message = null)
{
    /// <summary>就绪。</summary>
    public static BridgeLaunchResult Ready { get; } = new(BridgeLaunchState.Ready);
}

/// <summary>
/// 读取 helper 写在桥目录的启动状态文件（2026-10-20 复审）：helper 在游戏进程创建成功后写
/// <c>READY</c>，在创建前失败写 <c>ERROR:&lt;code&gt;:&lt;message&gt;</c>。launcher 等待 READY/ERROR，
/// 而不是把 helper 的退出码当作桥失败——helper 会把游戏自身的退出码（例如启动校验拒绝的 5）
/// 原样透传，只看退出码会把"游戏已启动但拒绝了自己"误报成桥故障（Windows 与 Linux 同型）。
/// 状态文件是 **launcher 的成功判据**（不是纯诊断）：helper 未写出 READY 时，即使游戏已启动，
/// launcher 也只能按超时报错；helper 在写状态前崩溃同样只能等满超时（默认 30 s）——这是为了
/// 不再用退出码误判而接受的延迟。
/// </summary>
public static class BridgeLaunchStatus
{
    /// <summary>
    /// 默认等待上限：Linux 容器首次启动实测约 9 秒，30 秒留足冷启动/杀毒扫描余量。
    /// helper 在写状态前崩溃时，最坏要等满该上限才报错（不静默成功）。
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 为本次启动创建状态文件占位并返回主机路径。占位文件提前验证桥目录可写，也让测试/诊断
    /// 能定位本次会话；内容为空时读取方继续等待（helper 的原子 rename 会覆盖它）。调用方负责
    /// 在成功/失败/取消后清理（<see cref="WaitAsync"/> 会用 finally 清理；进程未启动成功时由
    /// launcher 清理）。
    /// </summary>
    public static string CreateStatusPath(string bridgeRoot)
    {
        Directory.CreateDirectory(bridgeRoot);
        var statusPath = Path.Combine(bridgeRoot, $"status-{Guid.NewGuid():N}.txt");
        File.WriteAllText(statusPath, string.Empty);
        return statusPath;
    }

    /// <summary>
    /// 轮询状态文件直到 READY / ERROR / 超时。READY 表示游戏进程已创建，即使 helper 随后以
    /// 游戏退出码（含 5）结束也不再是桥失败。无论正常返回、超时还是取消，都会删除本次状态文件。
    /// </summary>
    public static async Task<BridgeLaunchResult> WaitAsync(
        string statusPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var text = TryRead(statusPath);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (text.StartsWith(DecoyParentBridge.ReadyStatus, StringComparison.Ordinal))
                    {
                        return BridgeLaunchResult.Ready;
                    }

                    if (text.StartsWith(DecoyParentBridge.ErrorStatusPrefix, StringComparison.Ordinal))
                    {
                        var parts = text.Split(':', 3);
                        var exitCode = parts.Length > 1
                            && int.TryParse(
                                parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                                ? parsed
                                : (int?)null;
                        var message = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2])
                            ? parts[2]
                            : "helper 未提供失败详情";
                        return new BridgeLaunchResult(BridgeLaunchState.Failed, exitCode, message);
                    }
                }

                if (Stopwatch.GetElapsedTime(started) >= timeout)
                {
                    return new BridgeLaunchResult(BridgeLaunchState.TimedOut);
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            TryDelete(statusPath);
        }
    }

    /// <summary>读取状态文件；文件尚未创建/正被替换/权限失败都视为"暂不可读"。</summary>
    private static string? TryRead(string path)
    {
        try
        {
            // 必须允许 helper 并发 rename 覆盖（Windows 上 File.ReadAllText 的
            // FileShare.Read 不允许删除/改名，会让 helper 的原子写静默失败 → 误超时）
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>清理本次会话的状态文件（路径为 null/不存在/删除失败均静默；失败不影响启动结果）。</summary>
    internal static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
        }
    }
}
