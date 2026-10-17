namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>
/// 包式渠道的 v2 增量载荷本轮不可用：<b>当前安装不适用</b>（基文件缺失/内容与官方清单不符、
/// 只读校验节点缺文件/尺寸不符等。见 <see cref="DeltaPackageUnusableException"/> 的另一类：
/// 载荷自身不可用）。调用方应回退整包更新，而不是把它当致命错误抛给用户——整包永远可用。
/// </summary>
public class DeltaPackageNotApplicableException(string message, Exception? innerException = null)
    : UpdateException(message, innerException);
