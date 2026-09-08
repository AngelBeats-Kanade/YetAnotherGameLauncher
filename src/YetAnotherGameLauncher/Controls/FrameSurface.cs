using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.Controls;

/// <summary>
/// 背景视频帧渲染面：把 <see cref="IVideoBackdropPlayer"/> 的当前帧按 UniformToFill + 左上锚定绘制
/// （与详情页海报 Image 同语义：溢出只裁右侧/底部，左缘完整贴住侧栏）。帧由播放器在线程池解码并
/// 原地更新位图内容，控件订阅 FrameUpdated 触发重绘，无 airspace/圆角裁剪问题。
/// </summary>
public class FrameSurface : Control
{
    /// <summary>播放器实例（DataContext 的 VideoPlayer 属性）。</summary>
    public static readonly StyledProperty<IVideoBackdropPlayer?> PlayerProperty =
        AvaloniaProperty.Register<FrameSurface, IVideoBackdropPlayer?>(nameof(Player));

    /// <summary>播放器实例（DataContext 的 VideoPlayer 属性）。</summary>
    public IVideoBackdropPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    /// <summary>播放器切换时重挂帧通知订阅。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlayerProperty)
        {
            if (change.OldValue is IVideoBackdropPlayer oldPlayer)
            {
                oldPlayer.FrameUpdated -= OnFrameUpdated;
            }

            if (change.NewValue is IVideoBackdropPlayer newPlayer)
            {
                newPlayer.FrameUpdated += OnFrameUpdated;
            }

            InvalidateVisual();
        }
    }

    /// <summary>控件从视觉树移除时退订，避免持有已销毁控件的引用。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Player is { } player)
        {
            player.FrameUpdated -= OnFrameUpdated;
        }

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>控件重新挂回视觉树时恢复订阅。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Player is { } player)
        {
            player.FrameUpdated += OnFrameUpdated;
        }
    }

    private void OnFrameUpdated(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>UniformToFill + 左上锚定绘制当前帧（缩放取两轴较大者，溢出裁右侧/底部）。</summary>
    public override void Render(DrawingContext context)
    {
        if (Player?.Frame is not { } frame || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var size = frame.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var scale = Math.Max(Bounds.Width / size.Width, Bounds.Height / size.Height);
        context.DrawImage(frame, new Rect(0, 0, size.Width * scale, size.Height * scale));
    }
}
