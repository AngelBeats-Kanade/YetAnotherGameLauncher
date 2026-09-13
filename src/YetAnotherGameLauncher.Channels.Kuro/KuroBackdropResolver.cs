using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>
/// 鸣潮详情页背景解析：直连官方启动器运营配置（launcher-config 取当期投放哈希 → 背景内容 JSON，
/// 含背景视频 + 首帧图，随官方投放即时更新）。与终末地一致的单级模型——视频不可用时首帧图即一级回退，
/// 解析失败返回 null，由上层回退上次磁盘缓存与主题渐变。
/// </summary>
public sealed class KuroBackdropResolver(KuroSwitchConfigClient switchConfigClient) : IBackdropResolver
{
    public async Task<BackdropSource?> GetBackdropUrlAsync(BackdropRequest request, CancellationToken cancellationToken = default)
    {
        var config = await switchConfigClient.FetchAsync(
            request.ServerOptions.GetValueOrDefault(KuroChannelApi.IndexUrlOptionKey),
            request.Region, cancellationToken).ConfigureAwait(false);
        return config is null ? null : AsSource(config);
    }

    /// <summary>
    /// 背景来源：视频类型（backgroundFileType=2 或缺省）带首帧占位图（无首帧时由 UI 回退主题渐变，
    /// 视频首帧到达后无缝替换）；其余类型按静态图处理。
    /// </summary>
    private static BackdropSource? AsSource(KuroSwitchConfig config) => config.IsVideo
        ? new BackdropSource(
            config.BackgroundFile, BackdropKind.Video,
            string.IsNullOrWhiteSpace(config.FirstFrameImage) ? null : config.FirstFrameImage)
        : BackdropSource.ImageOrNullIfEmpty(config.BackgroundFile);
}
