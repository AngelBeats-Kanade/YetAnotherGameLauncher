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

    /// <summary>
    /// 状态是否由安装/更新流程确认写出的。false = 仅由"检测到现有文件 → 登记为渠道最新版"
    /// 的零校验流程写入：内容未经任何核对，UI 不得把它呈现成"已是最新版本"
    /// （2026-10-17 半更新掩盖事故立案，2026-10-18 落地）。旧文件缺该字段时缺省为 true，
    /// 语义只作用于新写入的登记态。
    /// </summary>
    public bool Verified { get; set; } = true;
}
