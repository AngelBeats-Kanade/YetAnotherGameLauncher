using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 这些测试都经由 VmFactory 创建 MainWindowViewModel，其构造会把 LocBridge.Instance
/// 指向自己的本地化实例（静态桥）；并行运行会互相替换导致绑定串实例，故强制顺序执行。
/// </summary>
[CollectionDefinition("sequential", DisableParallelization = true)]
public sealed class SequentialTestCollection;
