namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>背景解析请求：游戏标识、渠道、目标区域与该游戏配置里对应服务器的选项。</summary>
/// <param name="GameId">游戏 kebab-case id（如 "wuthering-waves"）。</param>
/// <param name="Channel">渠道实现键（games.json 的 game.channel，对应 DI 中注册的 IBackdropResolver）。</param>
/// <param name="Region">"cn" 或 "global"，由界面语言决定。</param>
/// <param name="InstallDir">游戏安装目录（本地缓存探测类解析需要）。</param>
/// <param name="ServerOptions">games.json 中该区域对应 server 的 options（渠道端点参数）。</param>
public sealed record BackdropRequest(
    string GameId,
    string Channel,
    string Region,
    string? InstallDir,
    IReadOnlyDictionary<string, string> ServerOptions);

/// <summary>
/// 按区域解析游戏的详情页背景图来源（当期版本/卡池主视觉等）。
/// 实现按渠道放在渠道项目里，按渠道键注册；返回 http(s) 直链或已存在的本地文件路径，null = 暂无法确定。
/// </summary>
public interface IBackdropResolver
{
    /// <summary>解析指定游戏与区域的背景图；返回 http(s) 直链或本地文件路径，null = 暂无法确定。</summary>
    Task<string?> GetBackdropUrlAsync(BackdropRequest request, CancellationToken cancellationToken = default);
}
