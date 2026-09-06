namespace YetAnotherGameLauncher.Core.Models;

/// <summary>games.json 根文档。</summary>
public sealed class GameCatalog
{
    /// <summary>全局设置（安装根目录、主题、下载并发等）。</summary>
    public AppSettings Settings { get; set; } = new();

    /// <summary>游戏定义列表（可为空）。</summary>
    public List<GameDefinition> Games { get; set; } = [];
}
