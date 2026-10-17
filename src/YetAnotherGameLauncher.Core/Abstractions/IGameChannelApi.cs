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

    /// <summary>
    /// 获取预下载（下一版本）清单；预下载不可用或渠道不适用时返回 null。
    /// 包式渠道（终末地）实现此方法；差分式渠道（鸣潮）走 GetIncrementalManifestAsync，默认返回 null。
    /// </summary>
    Task<GameManifest?> GetPredownloadManifestAsync(
        GameServer server, CancellationToken cancellationToken = default)
        => Task.FromResult<GameManifest?>(null);

    /// <summary>
    /// 获取 "<paramref name="fromVersion"/> → <paramref name="toVersion"/>" 过渡中官方点名的废弃文件
    /// （包式渠道：补丁包内的 delete_files.txt；更新落地后删除它们才不残留旧版本文件）。
    /// 该渠道不适用（文件式渠道的废弃文件随增量清单 DeleteFiles 下发）或暂时取不到时返回 null——
    /// 清理是尽力而为，调用方不得因 null/失败让更新失败。默认返回 null。
    /// </summary>
    Task<IReadOnlyList<string>?> GetObsoleteFilesAsync(
        GameServer server, string fromVersion, string toVersion, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>?>(null);

}
