using Avalonia.Media;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 被替换 UI 位图的宽限退役队列（F35）：绑定解除后合成器仍可能持有在途渲染引用，
/// 立即 Dispose 有 use-after-free 面（与播放器 d81aaa5 的 RetireGrace 同惯例）。
/// 所有权契约：谁放弃引用谁负责退役——<see cref="BackgroundImageService"/> 缓存
/// 不拥有位图生命周期、永不 Dispose；UI 侧替换图像属性时把旧位图交本队列，宽限后释放。
/// </summary>
internal sealed class ImageRetireQueue(TimeProvider? timeProvider = null, TimeSpan? grace = null)
{
    /// <summary>退役宽限：覆盖合成器在途帧的渲染周期（与播放器 RetireGrace 同量级）。</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly List<(IImage Image, long RetiredAt)> _retired = [];
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _grace = grace ?? DefaultGrace;

    /// <summary>把被替换的位图入队（立即返回不阻塞 UI）；null 忽略，同实例重复入队幂等。</summary>
    public void Retire(IImage? image)
    {
        if (image is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_retired.Any(r => ReferenceEquals(r.Image, image)))
            {
                return;
            }

            _retired.Add((image, _time.GetTimestamp()));
        }
    }

    /// <summary>释放超过宽限期的退役位图，返回释放数。IImage 本身无 Dispose（如测试桩）自动跳过。</summary>
    public int FlushExpired()
    {
        List<IImage>? doomed = null;
        lock (_gate)
        {
            for (var i = _retired.Count - 1; i >= 0; i--)
            {
                // 年龄经 GetElapsedTime 按时间源频率换算（F13 同教训：不得拿 QPC 差当 TimeSpan tick）
                if (_time.GetElapsedTime(_retired[i].RetiredAt) >= _grace)
                {
                    (doomed ??= []).Add(_retired[i].Image);
                    _retired.RemoveAt(i);
                }
            }
        }

        if (doomed is null)
        {
            return 0;
        }

        var disposed = 0;
        foreach (var image in doomed)
        {
            if (image is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                    disposed++;
                }
                catch
                {
                    // 位图释放失败不影响 UI：随进程回收
                }
            }
        }

        return disposed;
    }

    /// <summary>调度一次延迟冲刷（替换点调用；与播放器终末冲刷同型，任务不跟踪不去重、幂等空转无害）。</summary>
    public void ScheduleFlush() => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(_grace + TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            FlushExpired();
        }
        catch
        {
            // 尽力而为：位图释放失败随进程回收
        }
    });
}
