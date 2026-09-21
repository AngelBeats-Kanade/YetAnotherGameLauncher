namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 启动门控的纯决策核心：判定"启动遮蔽层此刻能否放行进入主界面"。
/// 输入全部为调用方采集的快照（无锁、无副作用），供 <c>MainWindowViewModel.RunBootGateAsync</c>
/// 有界轮询调用，逻辑整体可单测。
/// 放行语义：就绪条件满足（选中游戏的背景视频首帧或静态海报任一就绪；无选中游戏/配置错误
/// 则无事可等）且已展示最小时长；超时无条件放行——首启下载（FFmpeg 库/视频）与慢网络的兜底，
/// 遮蔽放行后海报/渐变照常兜底、视频就绪后弹入。
/// </summary>
internal static class BootGate
{
    /// <summary>启动遮蔽最小展示时长（秒）：全热缓存下门控几乎立即就绪，无最小时长会一闪而过像闪烁。</summary>
    internal const double MinSplashSeconds = 0.4;

    /// <summary>门控轮询间隔（秒）：资产就绪以属性通知驱动的轮询发现，粒度无需更细。</summary>
    internal const double PollIntervalSeconds = 0.1;

    /// <summary>
    /// 判定遮蔽层此刻是否应放行。
    /// </summary>
    /// <param name="selectedGameReady">选中游戏就绪（视频首帧 <c>HasBackgroundVideo</c> 或海报 <c>HasBackgroundImage</c> 任一为真）。</param>
    /// <param name="hasSelectedGame">是否存在选中游戏（无游戏则无可等待的资产）。</param>
    /// <param name="configError">配置加载失败（主界面本身就是错误态，无需遮蔽等待）。</param>
    /// <param name="elapsedSeconds">遮蔽已展示时长（秒）。</param>
    /// <param name="timeoutSeconds">放行超时（秒）；到达即无条件放行。</param>
    /// <param name="minSplashSeconds">最小展示时长（秒）；缺省取 <see cref="MinSplashSeconds"/>。</param>
    /// <returns>true = 放行（遮蔽开始退场）。</returns>
    internal static bool ShouldRelease(
        bool selectedGameReady,
        bool hasSelectedGame,
        bool configError,
        double elapsedSeconds,
        double timeoutSeconds,
        double minSplashSeconds = MinSplashSeconds) =>
        elapsedSeconds >= timeoutSeconds
        || (elapsedSeconds >= minSplashSeconds
            && (configError || !hasSelectedGame || selectedGameReady));
}
