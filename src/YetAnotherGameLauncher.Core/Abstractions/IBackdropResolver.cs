namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>背景来源类型：静态图或视频（视频随附首帧海报作加载前占位）。</summary>
public enum BackdropKind
{
    /// <summary>静态图（http 直链或本地文件路径）。</summary>
    Image,

    /// <summary>视频（http 直链或本地文件路径；PosterUrl 提供首帧占位图）。</summary>
    Video,
}

/// <summary>背景来源：远程直链或本地文件路径，及其类型与（视频时的）首帧海报。</summary>
/// <param name="Url">背景本体来源：http(s) 直链或本地文件路径。</param>
/// <param name="Kind">背景类型（决定 UI 层用静态图还是视频播放管线呈现）。</param>
/// <param name="PosterUrl">视频加载前的占位图来源（官方首帧图/海报）；静态图类型为 null。</param>
public sealed record BackdropSource(string Url, BackdropKind Kind, string? PosterUrl = null)
{
    /// <summary>把静态图来源包装成 Image 类型的背景（渠道实现里最常见路径的便捷构造）。</summary>
    public static BackdropSource? ImageOrNullIfEmpty(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : new BackdropSource(url, BackdropKind.Image);
}

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
/// 按区域解析游戏的详情页背景来源（当期版本/卡池主视觉等）。
/// 实现按渠道放在渠道项目里，按渠道键注册；返回 http(s) 直链或已存在的本地文件路径，null = 暂无法确定。
/// </summary>
public interface IBackdropResolver
{
    /// <summary>解析指定游戏与区域的背景；返回直链/本地路径（含类型），null = 暂无法确定。</summary>
    Task<BackdropSource?> GetBackdropUrlAsync(BackdropRequest request, CancellationToken cancellationToken = default);
}
