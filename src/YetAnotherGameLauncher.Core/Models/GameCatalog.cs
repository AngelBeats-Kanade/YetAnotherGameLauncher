namespace YetAnotherGameLauncher.Core.Models;

/// <summary>games.json 根文档。</summary>
public sealed class GameCatalog
{
    public AppSettings Settings { get; set; } = new();

    public List<GameDefinition> Games { get; set; } = [];
}
