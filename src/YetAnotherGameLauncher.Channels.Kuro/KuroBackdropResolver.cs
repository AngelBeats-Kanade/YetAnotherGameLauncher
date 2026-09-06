using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>
/// 鸣潮的详情页背景解析：探测本机官方启动器的背景帧缓存（kr_game_cache/animate_bg，当期版本主视觉，
/// 官方启动器更新时自动轮换）。库洛未开放免登录的当期卡池立绘接口（库街区相关接口需账号令牌），
/// 官方启动器同款主视觉是可稳定获取的当期官方背景；每次打开都重新探测最新帧，即"检查更新"。
/// </summary>
public sealed class KuroBackdropResolver : IBackdropResolver
{
    public Task<string?> GetBackdropUrlAsync(BackdropRequest request, CancellationToken cancellationToken = default)
    {
        var frame = KuroLauncherBackground.FindLatestFrame(request.InstallDir);
        return Task.FromResult(frame);
    }
}
