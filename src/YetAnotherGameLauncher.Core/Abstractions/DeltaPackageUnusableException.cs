namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>
/// v2 增量载荷自身不可用（条目读不出、产出与官方清单不符等）：同族处置 = 回退整包，
/// 但与 <see cref="DeltaPackageNotApplicableException"/> 的"安装不适用"不同，这是**异常信号**——
/// 重试同一份计划不会自愈，调用方应按异常记 Warning 以便诊断（否则载荷侧的问题会以
/// "用户重试恒失败"或"静默多下几十 GiB"两种坏形态之一存在，2026-10-17 复审）。
/// </summary>
public sealed class DeltaPackageUnusableException(string message, Exception? innerException = null)
    : DeltaPackageNotApplicableException(message, innerException);
