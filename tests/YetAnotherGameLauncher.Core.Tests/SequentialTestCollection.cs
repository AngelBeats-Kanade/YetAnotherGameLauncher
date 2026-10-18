using Xunit;

namespace YetAnotherGameLauncher.Core.Tests;

/// <summary>
/// 强制顺序执行：这些类共享**进程级静态测试缝**（<c>MultiPartZipEntryReader.MaxEntryBytesForTests</c>、
/// <c>RemoteZipEntryReader.TailWindowBytesForTests/MaxResponseBodyBytesForTests</c>、
/// <c>PackageInstallerService.StagedArchiveRemovedBetweenCheckAndProbeForTests</c>、
/// <c>ManifestVerifier.FileRemovedBetweenCheckAndProbeForTests</c>、
/// <c>DiskSpace.AvailableFreeBytesForTests</c>）——并行窗口里一个测试的钩子
/// （压小上限、删目录、删文件）会波及另一个测试的同路径调用，造成假红/假 null
/// （复审 P3-3 与第六轮 P3-1，2026-10-17）。
/// 设置或经过这些代码路径的测试类都必须加入本集合；**关键是找齐 setter**（集合与非集合互斥，
/// setter 入集合即关闭两个方向）。与 App.Tests 的同名集合同型（本仓先例）。
/// </summary>
[CollectionDefinition("sequential", DisableParallelization = true)]
public sealed class SequentialTestCollection;
