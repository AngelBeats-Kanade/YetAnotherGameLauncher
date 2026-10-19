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

    /// <summary>
    /// 获取指定版本的全量清单；<paramref name="resourceTier"/> 非空时按档位裁剪/追加
    /// （鸣潮：去掉未选中档位目录、补上选中档位包；渠道不支持档位时为纯展示参数，忽略即可）。
    /// </summary>
    Task<GameManifest> GetManifestAsync(
        GameServer server, string version, string? resourceTier = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取 "fromVersion → toVersion" 的增量清单；该差分组合不可用时返回 null。
    /// <paramref name="resourceTier"/> 语义同 <see cref="GetManifestAsync"/>（增量侧只做
    /// "去掉未选中档位目录"的裁剪：档位包自身的落盘由更新后的全量修复轮补齐）。
    /// </summary>
    Task<GameManifest?> GetIncrementalManifestAsync(
        GameServer server, string fromVersion, string toVersion, string? resourceTier = null,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// 获取 "<paramref name="fromVersion"/> → <paramref name="toVersion"/>" 的 v2 增量更新计划
    /// （包式渠道：官方 patches 载荷 + cd_key + 逐文件清单，终末地 8.72 GiB 替代整包数十 GiB）。
    /// 不适用/不可得返回 null——调用方回退整包更新。默认返回 null（文件式渠道不用）。
    /// </summary>
    Task<DeltaPackagePlan?> GetDeltaPackageAsync(
        GameServer server, string fromVersion, string toVersion, CancellationToken cancellationToken = default)
        => Task.FromResult<DeltaPackagePlan?>(null);

    /// <summary>
    /// 该服务器是否支持按档位下载资源包（鸣潮官方档位索引 resourcePacks）。false = 档位只作
    /// 启动参数、下载恒为官方默认 bundle（common + HD）；设置页据此给出提示、清理动作不可用。
    /// 默认 false。
    /// </summary>
    bool SupportsResourcePackDownload(GameServer server) => false;

    /// <summary>
    /// 获取指定档位资源包的清单（该档位需要落盘的文件；官方实测每档 100 个）。用于测体积与
    /// 显式「清理其他档位」；渠道不支持按档位下载、或该档位不在官方档位索引里时返回 null。
    /// 默认 null。
    /// </summary>
    Task<ResourcePackManifest?> GetResourcePackManifestAsync(
        GameServer server, string tier, CancellationToken cancellationToken = default)
        => Task.FromResult<ResourcePackManifest?>(null);
}
