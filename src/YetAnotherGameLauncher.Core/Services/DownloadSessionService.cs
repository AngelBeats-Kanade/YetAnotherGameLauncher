using System.Text.Json;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>下载会话的操作种类（写标记 + 续传分派共用；字符串单一事实源）。</summary>
public static class DownloadSessionKind
{
    /// <summary>全新安装。</summary>
    public const string Install = "install";

    /// <summary>更新/校验修复。</summary>
    public const string Update = "update";

    /// <summary>预下载（只暂存、不改动游戏本体）。</summary>
    public const string Predownload = "predownload";

    /// <summary>应用已暂存的预下载。</summary>
    public const string Apply = "apply";
}

/// <summary>一次"会下载并落盘"的操作记录（{installDir}/.yagl/download-session.json）。</summary>
public sealed class DownloadSession
{
    /// <summary>操作种类（见 <see cref="DownloadSessionKind"/>）：UI 据此把「继续下载」派回被暂停的那个动作
    /// （预下载暂停后回到预下载链，不得改走更新链——两段式契约，2026-10-19 review）。</summary>
    public string Kind { get; set; } = "";

    /// <summary>该次操作的目标版本（诊断 + 暂存清单归属核对用）。</summary>
    public string Version { get; set; } = "";

    /// <summary>写入标记的游戏 id（同一安装目录被多游戏/多服务器复用时核对归属；旧标记缺省空 = 任意）。</summary>
    public string GameId { get; set; } = "";

    /// <summary>写入标记的服务器 id（同上）。</summary>
    public string ServerId { get; set; } = "";

    /// <summary>开始时间（UTC）。</summary>
    public DateTimeOffset StartedAt { get; set; }
}

/// <summary>
/// 下载会话标记（2026-10-19 用户需求：下载中关停应用后重开，从上次暂停处继续而不是从头下载）。
/// 操作开始时写、<b>成功收尾时清</b>；取消 / 失败 / 进程被杀都留存——重开应用据此提示"上次下载未完成"，
/// 主按钮呈现「继续下载」。
/// </summary>
/// <remarks>
/// 标记只承载"有没有未完成的操作"，<b>不承载进度</b>：进度与产物一律以磁盘为准——
/// 完成文件由快速校验（存在性 + 尺寸）跳过、半成品由 <c>.temp</c> + HTTP Range 续传、
/// 预载/整包暂存按 size/MD5 复用（重跑幂等），因此从"哪里断的"继续是重跑同一动作的自然结果。
/// 标记损坏按"无标记"处理（最坏只是少一次提示，不阻塞任何操作）。
/// </remarks>
public sealed class DownloadSessionService(string installDir)
{
    /// <summary>标记文件名（与暂存清单同目录 .yagl 下）。</summary>
    public const string FileName = "download-session.json";

    private string FilePath => Path.Combine(installDir, LocalStateService.StateDirName, FileName);

    /// <summary>开始一次会下载/落盘的操作（写标记；同目录重复开始幂等覆盖）。</summary>
    public void Begin(string kind, string version = "", string gameId = "", string serverId = "")
    {
        try
        {
            var stateDir = Path.Combine(installDir, LocalStateService.StateDirName);
            Directory.CreateDirectory(stateDir);
            var payload = new DownloadSession
            {
                Kind = kind,
                Version = version,
                GameId = gameId,
                ServerId = serverId,
                StartedAt = DateTimeOffset.UtcNow,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(payload, Json.Default));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 标记写不出只影响"下次是否提示续传"：不阻断操作本身
        }
    }

    /// <summary>操作成功收尾：清掉标记（此后主按钮不再是「继续下载」）。</summary>
    public void Clear() => FileUtilities.DeleteQuiet(FilePath);

    /// <summary>
    /// 读取标记并核对归属：同一安装目录可能被多个服务器复用（切服只换状态上下文，目录不变），
    /// 标记只对写入它的游戏/服务器有效——旧标记缺归属字段时按"任意归属"处理（升级不丢续传提示）。
    /// </summary>
    public DownloadSession? LoadFor(string gameId, string serverId)
    {
        var session = Load();
        if (session is null)
        {
            return null;
        }

        var legacy = string.IsNullOrEmpty(session.GameId) && string.IsNullOrEmpty(session.ServerId);
        return legacy || (session.GameId == gameId && session.ServerId == serverId) ? session : null;
    }

    /// <summary>读取标记；没有 / 损坏 / 不可读一律返回 null（按无标记处理）。</summary>
    public DownloadSession? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var session = JsonSerializer.Deserialize<DownloadSession>(File.ReadAllText(FilePath), Json.Default);
            return session is null || string.IsNullOrEmpty(session.Kind) ? null : session;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
