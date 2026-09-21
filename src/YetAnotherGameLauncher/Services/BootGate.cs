namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 启动门控的纯决策核心：判定"启动遮蔽层此刻能否放行进入主界面"。
/// 输入全部为调用方采集的快照（无锁、无副作用），供 <c>MainWindowViewModel.RunBootGateAsync</c>
/// 有界轮询调用，逻辑整体可单测。
/// 放行语义（2026-09-21 调优：优先视频——海报就绪即放行会让用户先看静态图、
/// 视频再弹入，静态→动态的跳变正是观感痛点）：背景视频首帧就绪（<c>HasBackgroundVideo</c>）
/// 即候补放行；静态海报（<c>HasBackgroundImage</c>）只在宽限期后作为兜底候补
/// （视频起播通常比海报晚零点几秒，宽限期给视频留出胜出窗口）；无选中游戏/配置错误
/// 无事可等到最小时长即放行；超时无条件放行——首启下载（FFmpeg 库/视频）与慢网络的兜底，
/// 遮蔽放行后海报/渐变照常兜底、视频就绪后弹入。
/// </summary>
internal static class BootGate
{
    /// <summary>启动遮蔽最小展示时长（秒）：全热缓存下门控几乎立即就绪，无最小时长会一闪而过像闪烁。</summary>
    internal const double MinSplashSeconds = 0.4;

    /// <summary>静态海报的放行宽限（秒）：海报先于视频就绪时不立即放行，等视频首帧到
    /// 宽限期为止——消除"进入后静态图→视频弹入"的跳变；超宽限仍无视频则海报兜底放行。</summary>
    internal const double PosterGraceSeconds = 1.5;

    /// <summary>门控轮询间隔（秒）：资产就绪以属性通知驱动的轮询发现，粒度无需更细。</summary>
    internal const double PollIntervalSeconds = 0.1;

    /// <summary>
    /// 判定遮蔽层此刻是否应放行。
    /// </summary>
    /// <param name="videoReady">选中游戏视频首帧就绪（<c>HasBackgroundVideo</c>）。</param>
    /// <param name="posterReady">选中游戏静态海报就绪（<c>HasBackgroundImage</c>）。</param>
    /// <param name="hasSelectedGame">是否存在选中游戏（无游戏则无可等待的资产）。</param>
    /// <param name="configError">配置加载失败（主界面本身就是错误态，无需遮蔽等待）。</param>
    /// <param name="elapsedSeconds">遮蔽已展示时长（秒）。</param>
    /// <param name="timeoutSeconds">放行超时（秒）；到达即无条件放行。</param>
    /// <param name="minSplashSeconds">最小展示时长（秒）；缺省取 <see cref="MinSplashSeconds"/>。</param>
    /// <param name="posterGraceSeconds">海报放行宽限（秒）；缺省取 <see cref="PosterGraceSeconds"/>。</param>
    /// <returns>true = 放行（遮蔽开始退场）。</returns>
    internal static bool ShouldRelease(
        bool videoReady,
        bool posterReady,
        bool hasSelectedGame,
        bool configError,
        double elapsedSeconds,
        double timeoutSeconds,
        double minSplashSeconds = MinSplashSeconds,
        double posterGraceSeconds = PosterGraceSeconds) =>
        elapsedSeconds >= timeoutSeconds
        || (elapsedSeconds >= minSplashSeconds && (videoReady || configError || !hasSelectedGame))
        || (posterReady && elapsedSeconds >= posterGraceSeconds);
}
