using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

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
    BridgeLaunchState State, int? ExitCode = null, string? Message = null, bool HelperStarted = false)
{
    /// <summary>就绪。</summary>
    public static BridgeLaunchResult Ready { get; } = new(BridgeLaunchState.Ready, HelperStarted: true);
}

/// <summary>
/// 读取 helper 写在桥目录的启动状态文件（2026-10-20 复审）：helper 在游戏进程创建成功后写
/// <c>READY</c>，在创建前失败写 <c>ERROR:&lt;code&gt;:&lt;message&gt;</c>。launcher 等待 READY/ERROR，
/// 而不是把 helper 的退出码当作桥失败——helper 会把游戏自身的退出码（例如启动校验拒绝的 5）
/// 原样透传，只看退出码会把"游戏已启动但拒绝了自己"误报成桥故障（Windows 与 Linux 同型）。
/// 状态文件是 **launcher 的成功判据**（不是纯诊断）：helper 未写出 READY 时，即使游戏已启动，
/// launcher 也只能按超时报错；helper 在写状态前崩溃只能等满启动上限，写出 STARTING 后崩溃
/// 则等满 READY 上限（两段各 10 s）——这是为了不再用退出码误判而接受的延迟。
/// </summary>
public static class BridgeLaunchStatus
{
    /// <summary>
    /// 默认 READY 等待上限（helper 已写出 STARTING 后）：正常单实例启动时游戏进程创建
    /// 远低于 10 秒；2026-10-21 用户口径为 10 秒。与 helper 启动等待分开计时，见
    /// <see cref="DefaultStartupTimeout"/>。
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 默认 helper 启动等待上限：Linux 链的计时从容器入口（<c>_v2-entry-point</c>）启动算起，
    /// 含 Proton/umu 容器冷启动。2026-10-21 真机彻底冷启动实测首个桥状态 READY 4062 ms
    /// （已包含 helper 启动 + 游戏进程创建，启动阶段只会更短），10 秒预算余量约 5.9 秒；
    /// 仓库 2026-10-20 旧注释的约 9 秒是更早的历史快照。2026-10-21 用户明确指定 10 秒；
    /// 若真机再报「未启动」，先看启动日志的 “Bridge helper first status ... after N ms”
    /// 或 “Bridge helper startup timeout ... after N ms” 行（<see cref="WaitAsync"/> 在首次
    /// 观察到状态与启动超时时都记录 elapsed 与预算）。helper 写出首个状态后改按
    /// <see cref="DefaultTimeout"/> 计时——旧的单一 30 秒上限把 helper 启动
    /// 与游戏进程创建合并在同一总预算里（任一段变慢都会挤占另一段）；74 秒是 READY 之后游戏
    /// 自身的单实例等待，不属于 launcher 的状态等待，固定 30 秒上限也不会等 74 秒。分开后各
    /// 10 秒、互不挤占。生产两个启动器都显式传入本常量；<see cref="WaitAsync"/> 省略
    /// <c>startupTimeout</c> 时把调用方的 <c>timeout</c> 当作总预算（而非本常量）：
    /// 首个状态出现后 READY 段只用剩余时间，不重新获得一整段 timeout。
    /// </summary>
    public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(10);

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
    /// 两段轮询状态文件（2026-10-21）：helper 写出首个状态前按 <paramref name="startupTimeout"/>
    /// 计时（Linux 链含 Proton/umu 容器冷启动；省略时与 <paramref name="timeout"/> 相同，且
    /// <paramref name="timeout"/> 是总预算）。生产启动器显式传
    /// <see cref="DefaultStartupTimeout"/>；出现非空状态（STARTING）后，显式 startupTimeout
    /// 的调用方改按 <paramref name="timeout"/> 重新计时（游戏进程创建，默认 10 s），省略的
    /// 调用方继续使用总预算的剩余时间。READY 表示游戏进程已创建，
    /// 即使 helper 随后以游戏退出码（含 5）结束也不再是桥失败。超时结果用
    /// <see cref="BridgeLaunchResult.HelperStarted"/> 区分两段。无论正常返回、超时还是取消，
    /// 都会删除本次状态文件。
    /// </summary>
    public static async Task<BridgeLaunchResult> WaitAsync(
        string statusPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        TimeSpan? startupTimeout = null,
        ILogger? logger = null)
    {
        var startupLimit = startupTimeout ?? timeout;
        // 省略 startupTimeout = 单一总预算：首状态后不重置计时；显式传入才按两段各自的
        // 上限计时（生产启动器走后者）。
        var sharedBudget = startupTimeout is null;
        try
        {
            var started = Stopwatch.GetTimestamp();
            var phaseStarted = started;
            var helperStarted = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var text = TryRead(statusPath);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (!helperStarted)
                    {
                        // 阶段边界可诊断（2026-10-21）：10 秒预算是否擦边，需要知道首个状态出现
                        // 在第几毫秒；helper 的 STARTING 只写状态文件，不进 stdout 日志。
                        helperStarted = true;
                        var startupElapsed = Stopwatch.GetElapsedTime(started);
                        logger?.LogInformation(
                            "Bridge helper first status {Status} after {ElapsedMilliseconds:0} ms " +
                            "(startup budget {StartupBudgetMilliseconds:0} ms)",
                            text.Trim(),
                            startupElapsed.TotalMilliseconds,
                            startupLimit.TotalMilliseconds);
                        if (!sharedBudget)
                        {
                            phaseStarted = Stopwatch.GetTimestamp();
                        }
                    }

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
                        return new BridgeLaunchResult(
                            BridgeLaunchState.Failed, exitCode, message, HelperStarted: true);
                    }
                }

                var limit = helperStarted ? timeout : startupLimit;
                var phaseElapsed = Stopwatch.GetElapsedTime(phaseStarted);
                if (phaseElapsed >= limit)
                {
                    // 启动超时时状态文件始终为空，first-status 行不会出现；这里补一条带
                    // elapsed/limit 的日志，否则真机报「未启动」时没有边界可核对。
                    logger?.LogWarning(
                        "Bridge helper {Phase} timeout after {ElapsedMilliseconds:0} ms " +
                        "(limit {LimitMilliseconds:0} ms, sharedBudget={SharedBudget})",
                        helperStarted ? "READY" : "startup",
                        phaseElapsed.TotalMilliseconds,
                        limit.TotalMilliseconds,
                        sharedBudget);
                    return new BridgeLaunchResult(
                        BridgeLaunchState.TimedOut, HelperStarted: helperStarted);
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            TryDelete(statusPath);
        }
    }

    /// <summary>
    /// 统一生成两段超时的用户提示（2026-10-21）：helper 未启动走启动预算文案；helper 已启动
    /// 但未 READY 走游戏进程创建文案。调用方只提供各自平台的启动失败原因，避免两条启动链
    /// 各维护一份模板导致漏改。
    /// </summary>
    /// <param name="result">超时结果（<see cref="BridgeLaunchState.TimedOut"/>）。</param>
    /// <param name="readyTimeout">READY 段上限。</param>
    /// <param name="startupTimeout">helper 启动段上限。</param>
    /// <param name="logPath">启动日志路径。</param>
    /// <param name="startupFailureReason">启动失败括号内的平台特有原因。</param>
    public static string DescribeTimeout(
        BridgeLaunchResult result,
        TimeSpan readyTimeout,
        TimeSpan startupTimeout,
        string logPath,
        string startupFailureReason)
    {
        if (result.HelperStarted)
        {
            return $"诱饵父进程桥已启动，但游戏进程在 {readyTimeout.TotalSeconds:0} 秒内未创建" +
                   $"（游戏可能仍在启动，也可能已失败）。请查看启动日志：{logPath}";
        }

        return $"诱饵父进程桥在 {startupTimeout.TotalSeconds:0} 秒内未启动" +
               $"（{startupFailureReason}）。请查看启动日志：{logPath}";
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
