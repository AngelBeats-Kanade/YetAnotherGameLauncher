namespace YetAnotherGameLauncher.Core.Models;

/// <summary>某游戏某服务器在本地安装目录中的状态（{installDir}/.yagl/state.json）。</summary>
public sealed class LocalGameState
{
    public string GameId { get; set; } = "";

    public string ServerId { get; set; } = "";

    public string Version { get; set; } = "";
}
