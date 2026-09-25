using Avalonia;
using Avalonia.Media;
using Xunit;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// F35（artifacts/bugs.md）：被替换 UI 位图的宽限退役队列。所有权契约 = 谁放弃引用谁负责
/// 退役（服务缓存永不 Dispose）；宽限期内不释放（合成器在途引用，d81aaa5 同惯例）、
/// 宽限后释放（现代码对被替换位图完全不释放 → finalizer 兜底延迟、原生内存抬高）。
/// </summary>
public class ImageRetireQueueTests
{
    /// <summary>可释放的 IImage 测试桩（记录 Dispose 次数）。注意必须显式声明 IDisposable——
    /// C# 不会从公开 Dispose 方法推断接口实现（本次调试实证：漏声明时 is IDisposable 为 false、
    /// 队列按契约正确跳过，测试误判为"未释放"）。</summary>
    private sealed class DisposableImage : IImage, IDisposable
    {
        public int Disposed { get; private set; }

        public void Dispose() => Disposed++;

        public Size Size => new(1, 1);

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }

    /// <summary>不可释放的 IImage 桩（模拟无 IDisposable 的实现，如纯矢量图）。</summary>
    private sealed class PlainImage : IImage
    {
        public Size Size => new(1, 1);

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }

    [Fact]
    public void Retire_WithinGrace_NotDisposed_AfterGrace_DisposedExactlyOnce()
    {
        var time = new ManualTimeProvider();
        var queue = new ImageRetireQueue(time, TimeSpan.FromSeconds(2));
        var image = new DisposableImage();

        queue.Retire(image);
        queue.Retire(image); // 幂等：重复入队不重复释放

        Assert.Equal(0, queue.FlushExpired()); // 宽限期内不释放（age 0 < 2s）

        time.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(1, queue.FlushExpired()); // 宽限满：本次释放 1 个
        Assert.Equal(1, image.Disposed);
        Assert.Equal(0, queue.FlushExpired()); // 已出队：再次冲刷为 0（不重复释放）
    }

    [Fact]
    public void Retire_NonDisposableOrNull_SkippedWithoutThrow()
    {
        // IImage 本身无 Dispose（测试桩/纯矢量实现）：跳过释放而非抛；null 忽略
        var queue = new ImageRetireQueue(new ManualTimeProvider(), TimeSpan.Zero);

        queue.Retire(new PlainImage());
        queue.Retire(null);

        Assert.Equal(0, queue.FlushExpired());
    }
}
