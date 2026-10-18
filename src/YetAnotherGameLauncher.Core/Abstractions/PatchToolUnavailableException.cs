namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>
/// 补丁工具（hpatchz）不可用：未安装、自备路径无效，或自动供给失败（下载/校验/平台不支持）。
/// 与"补丁执行失败"（非零退出 = 补丁内容对不上）区分开：这是<b>工具/环境侧</b>的问题，
/// 与应用到一半的载荷无关——调用方据此保留下载好的增量载荷供修复后重试
/// （2026-10-17 复审 P3-5 登记的可选设计，2026-10-18 落地）。
/// </summary>
public sealed class PatchToolUnavailableException(string message, Exception? inner = null)
    : UpdateException(message, inner);
