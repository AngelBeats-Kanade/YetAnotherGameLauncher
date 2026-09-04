using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>
/// 游戏渠道（厂商启动器）API 抽象。每个渠道项目（Kuro/Hypergryph…）提供实现，
/// 负责版本查询、清单获取与下载地址拼接；返回的清单条目已带完整下载 URL。
/// </summary>
public interface IGameChannelApi
{
    /// <summary>查询服务器版本信息（含差分入口与预下载状态）。</summary>
    Task<ChannelVersionInfo> GetVersionInfoAsync(GameServer server, CancellationToken cancellationToken = default);

    /// <summary>获取指定版本的全量清单。</summary>
    Task<GameManifest> GetManifestAsync(GameServer server, string version, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取 "fromVersion → toVersion" 的增量清单；该差分组合不可用时返回 null。
    /// </summary>
    Task<GameManifest?> GetIncrementalManifestAsync(
        GameServer server, string fromVersion, string toVersion, CancellationToken cancellationToken = default);
}
