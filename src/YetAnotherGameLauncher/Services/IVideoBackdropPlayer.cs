using Avalonia.Media;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 详情页背景视频播放器抽象：把解码后的帧以 <see cref="IImage"/>（WriteableBitmap）形式暴露给
/// 自绘渲染控件。实现负责按需准备原生库（必要时含首运下载）。
/// 测试用假实现替换，headless 不触达真实解码器。
/// </summary>
public interface IVideoBackdropPlayer
{
    /// <summary>当前帧（视频尺寸确定后创建，内容随播放持续更新）；未播放或 Stop 后为 null。</summary>
    IImage? Frame { get; }

    /// <summary>循环回卷的淡化层：上一循环的最后一帧，随播放逐帧淡出（消除循环接缝）；非淡化期为 null。</summary>
    IImage? FadeFrame { get; }

    /// <summary>淡化层当前不透明度（0-1，逐帧递减至 0）；非淡化期为 0。</summary>
    double FadeOpacity { get; }

    /// <summary>新帧就绪通知（UI 线程触发；渲染控件订阅它触发重绘）。</summary>
    event EventHandler? FrameUpdated;

    /// <summary>
    /// 后台起播本地视频文件：静音、循环、按需准备原生库（必要时含首运下载）。
    /// 契约澄清（次级 suspect 第 9 轮）：返回的 Task 在**播放会话结束**时完成（自然结束/
    /// 解码失败/被 Stop 或新一代 PlayAsync 取代），不是起播即返回——调用方以此在会话收尾
    /// 时校验退订。false = 未能起播或会话以失败告终（库不可用/文件损坏），调用方保持静态海报。
    /// </summary>
    /// <param name="videoPath">本地视频文件路径。</param>
    /// <param name="cancellationToken">外部取消令牌（应用退出）。</param>
    Task<bool> PlayAsync(string videoPath, CancellationToken cancellationToken = default);

    /// <summary>停止播放、取消解码循环并清空帧缓冲（渲染层立即回到海报/渐变兜底；
    /// 迟到的陈旧帧通知以空帧缓冲为证不再点亮视频层）。</summary>
    void Stop();

    /// <summary>
    /// 暂停播放：解码循环在下一帧处理点泊车（不占 CPU/GPU），帧缓冲与解码源原样保留——
    /// 切到非详情页时保活会话用。无活动会话时为 no-op；不清帧、不触发海报兜底。
    /// </summary>
    void Pause();

    /// <summary>恢复播放：唤醒泊车的解码循环并重定节拍基线（暂停时长不计入时间轴，
    /// 下一帧立即呈现后恢复 PTS 节拍）。与 <see cref="Pause"/> 配对，无会话时为 no-op。</summary>
    void Resume();

    /// <summary>是否有活动播放会话（PlayAsync 已启动且未停止/自然结束）：
    /// 调用方据此在重回详情页时选择 <see cref="Resume"/> 续播而非重新起播。</summary>
    bool IsSessionActive { get; }
}
