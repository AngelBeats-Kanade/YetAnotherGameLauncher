using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 强制顺序执行的真实理由：这些测试类共享进程级单例状态——HeadlessSession（Avalonia
/// headless 平台初始化非线程安全）、Avalonia 全局 Locator、以及涉 FFmpeg 原生库加载的
/// 测试会与 headless 初始化竞争（2026-09-20 实锤）。历史理由"LocBridge 静态桥"已随桥删除。
/// </summary>
[CollectionDefinition("sequential", DisableParallelization = true)]
public sealed class SequentialTestCollection;
