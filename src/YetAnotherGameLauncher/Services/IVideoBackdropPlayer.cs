using Avalonia.Media;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 详情页背景视频播放器抽象：把解码后的帧以 <see cref="IImage"/>（WriteableBitmap）形式暴露给
/// 自绘渲染控件。实现负责按需准备原生库（必要时含首运下载）。
/// 测试用假实现替换，headless 不触达真实解码器。
/// </summary>
public interface IVideoBackdropPlayer
{
    /// <summary>当前帧（视频尺寸确定后创建，内容随播放持续更新）；未播放为 null。</summary>
    IImage? Frame { get; }

    /// <summary>循环回卷的淡化层：上一循环的最后一帧，随播放逐帧淡出（消除循环接缝）；非淡化期为 null。</summary>
    IImage? FadeFrame { get; }

    /// <summary>淡化层当前不透明度（0-1，逐帧递减至 0）；非淡化期为 0。</summary>
    double FadeOpacity { get; }

    /// <summary>新帧就绪通知（UI 线程触发；渲染控件订阅它触发重绘）。</summary>
    event EventHandler? FrameUpdated;

    /// <summary>
    /// 后台起播本地视频文件：静音、循环、按需准备原生库（必要时含首运下载）。
    /// 返回 false 表示无法起播（库不可用/文件损坏），调用方保持静态海报。
    /// </summary>
    /// <param name="videoPath">本地视频文件路径。</param>
    /// <param name="cancellationToken">外部取消令牌（应用退出）。</param>
    Task<bool> PlayAsync(string videoPath, CancellationToken cancellationToken = default);

    /// <summary>停止播放并取消解码循环（帧缓冲保留，由调用方决定何时隐藏渲染层）。</summary>
    void Stop();
}
