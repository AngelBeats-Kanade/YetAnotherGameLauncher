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
    /// 本地内容对应的资源包档位（hd/sd/uhd，2026-10-19 档位下载功能起）：
    /// 由 Core 在安装/更新/校验修复成功后按"该次落到盘上的档位"写入
    /// （渠道不支持按档位下载时恒记 hd —— 内容就是官方默认 bundle = common + HD）。
    /// null = 未同步过档位信息（旧状态文件）：UI 视为"档位资源包未下载"，
    /// 点一次校验修复即可补上（只补缺失文件，不重下已有内容）。非鸣潮/非档位渠道不写。
    /// </summary>
    public string? ResourceTier { get; set; }

    /// <summary>
    /// 状态是否由安装/更新流程确认写出的。false = 仅由"检测到现有文件 → 登记为渠道最新版"
    /// 的零校验流程写入：内容未经任何核对，UI 不得把它呈现成"已是最新版本"
    /// （2026-10-17 半更新掩盖事故立案，2026-10-18 落地）。旧文件缺该字段时缺省为 true，
    /// 语义只作用于新写入的登记态。
    /// </summary>
    public bool Verified { get; set; } = true;
}
