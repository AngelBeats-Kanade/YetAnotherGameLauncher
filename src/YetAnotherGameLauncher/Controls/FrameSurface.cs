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

    /// <summary>是否已挂接视觉树（订阅只在挂接期间存在，脱树即完全退订）。</summary>
    private bool _attachedToVisualTree;

    /// <summary>当前持有帧通知订阅的播放器（订阅去重基准）。</summary>
    private IVideoBackdropPlayer? _subscribedPlayer;

    /// <summary>播放器切换时把订阅对齐到当前值（仅挂接期间持有订阅）。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlayerProperty)
        {
            SyncFrameSubscription();
            InvalidateVisual();
        }
    }

    /// <summary>控件从视觉树移除时完全退订，避免持有已销毁控件的引用。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attachedToVisualTree = false;
        SyncFrameSubscription();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>控件重新挂回视觉树时恢复订阅；已有帧（暂停保活重进详情页）立即重绘——
    /// 不等下一帧通知，暂停帧当场上屏。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attachedToVisualTree = true;
        SyncFrameSubscription();
        if (Player?.Frame is not null)
        {
            InvalidateVisual();
        }
    }

    /// <summary>把帧通知订阅对齐到（挂接状态 × 当前 Player）的组合；幂等——生产时序是
    /// DataTemplate 实例化先经绑定推值 Player 再挂树，属性变更/挂树/脱树任意交错下
    /// 订阅数至多为 1，脱树后为 0（否则脱树的渲染面会被保活中的播放器事件滞留）。</summary>
    private void SyncFrameSubscription()
    {
        if (_subscribedPlayer is { } subscribed)
        {
            subscribed.FrameUpdated -= OnFrameUpdated;
            _subscribedPlayer = null;
        }

        var player = _attachedToVisualTree ? Player : null;
        if (player is not null)
        {
            player.FrameUpdated += OnFrameUpdated;
            _subscribedPlayer = player;
        }
    }

    private void OnFrameUpdated(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>UniformToFill + 左上锚定绘制当前帧（缩放取两轴较大者，溢出裁右侧/底部）；
    /// 存在循环淡化层时，在新帧之上按递减不透明度叠画上一循环末帧，掩盖循环接缝。</summary>
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
        var destination = new Rect(0, 0, size.Width * scale, size.Height * scale);
        context.DrawImage(frame, destination);

        var fadeFrame = Player.FadeFrame;
        var fadeOpacity = Player.FadeOpacity;
        if (fadeFrame is not null && fadeOpacity > 0)
        {
            using (context.PushOpacity(fadeOpacity))
            {
                context.DrawImage(fadeFrame, destination);
            }
        }
    }
}
