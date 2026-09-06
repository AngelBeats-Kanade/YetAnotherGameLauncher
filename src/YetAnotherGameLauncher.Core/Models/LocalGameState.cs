namespace YetAnotherGameLauncher.Core.Models;

/// <summary>某游戏某服务器在本地安装目录中的状态（{installDir}/.yagl/state.json）。</summary>
public sealed class LocalGameState
{
    /// <summary>所属游戏 id。</summary>
    public string GameId { get; set; } = "";

    /// <summary>所属服务器 id。</summary>
    public string ServerId { get; set; } = "";

    /// <summary>当前已安装的版本号；尚未安装时为空。</summary>
    public string Version { get; set; } = "";
}
