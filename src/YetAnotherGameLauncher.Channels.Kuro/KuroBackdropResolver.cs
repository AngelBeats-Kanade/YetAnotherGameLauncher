using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>
/// 鸣潮详情页背景解析，优先级从高到低：
/// ① 直连官方启动器运营配置（switch.json，含当期背景视频 + 首帧图，随官方投放即时更新）；
/// ② 扫描本机 KRLauncher 的 WebView 缓存提取最后一份已知配置（官方启动器用过后即有，含持久化兜底）；
/// ③ 游戏目录旁 kr_game_cache/animate_bg 的本地帧序列（末帧静态图，历史行为保留）。
/// </summary>
public sealed class KuroBackdropResolver(
    KuroSwitchConfigClient switchConfigClient,
    ILogger<KuroBackdropResolver>? logger = null) : IBackdropResolver
{
    public async Task<BackdropSource?> GetBackdropUrlAsync(BackdropRequest request, CancellationToken cancellationToken = default)
    {
        // ① 官方运营配置直连：拿到即是最新投放，顺手持久化（Chromium 缓存淘汰后的兜底）
        var config = await switchConfigClient.FetchAsync(
            request.ServerOptions.GetValueOrDefault(KuroChannelApi.IndexUrlOptionKey), cancellationToken).ConfigureAwait(false);
        if (config is not null)
        {
            KuroLauncherBackground.PersistSwitchConfig(config);
            return AsVideoSource(config);
        }

        // ② 本机 WebView 缓存扫描（内部已含持久化兜底）：离线/官方配置改版时仍能给出最后投放
        var cached = KuroLauncherBackground.FindLatestSwitchConfig();
        if (cached is not null)
        {
            logger?.LogDebug("Kuro backdrop from local launcher cache: {Url}", cached.BackgroundFile);
            return AsVideoSource(cached);
        }

        // ③ 本地帧序列兜底
        var frame = KuroLauncherBackground.FindLatestFrame(request.InstallDir);
        return BackdropSource.ImageOrNullIfEmpty(frame);
    }

    /// <summary>背景视频 + 首帧占位图（无首帧时由 UI 回退主题渐变，视频首帧到达后无缝替换）。</summary>
    private static BackdropSource AsVideoSource(KuroSwitchConfig config) => new(
        config.BackgroundFile, BackdropKind.Video,
        string.IsNullOrWhiteSpace(config.FirstFrameImage) ? null : config.FirstFrameImage);
}
