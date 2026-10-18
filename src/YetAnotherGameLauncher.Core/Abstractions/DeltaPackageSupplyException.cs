namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>
/// v2 增量载荷本身没问题，但补丁工具（hpatchz）不可用——与
/// <see cref="DeltaPackageUnusableException"/>（载荷/产出与清单不符）分家：
/// 调用方<b>不得</b>清掉已下载的载荷暂存（数 GiB，修复工具后零重下即可重试），
/// 也不应静默回退整包（整包会白费掉这份载荷，且工具问题照样可能挡住后续链）。
/// 继承 <see cref="DeltaPackageNotApplicableException"/> 只为防御性兜底：任何仍按
/// "本轮不适用 → 回退整包"处理的旧调用点行为安全。
/// </summary>
public sealed class DeltaPackageSupplyException(string message, Exception? inner = null)
    : DeltaPackageNotApplicableException(message, inner);
