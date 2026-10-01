using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>同步收集 <see cref="UpdateProgress"/> 帧的 IProgress 实现：测试线程直接回调，
/// 不经任何同步上下文（Progress&lt;T&gt; 在部分测试宿主的同步上下文下 post 不可靠，
/// 帧到达时机不可控）。</summary>
public sealed class UpdateProgressCollector : IProgress<UpdateProgress>
{
    /// <summary>按到达顺序收集的全部进度帧。</summary>
    public List<UpdateProgress> Frames { get; } = [];

    /// <summary>记录一帧。</summary>
    public void Report(UpdateProgress value) => Frames.Add(value);
}
