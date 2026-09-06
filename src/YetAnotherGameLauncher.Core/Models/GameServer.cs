namespace YetAnotherGameLauncher.Core.Models;

/// <summary>游戏目录中的一个服务器/渠道入口（如国服、国际服）。</summary>
public sealed class GameServer
{
    /// <summary>服务器唯一标识（同一游戏内不重复，用作本地状态键的一部分）。</summary>
    public string Id { get; set; } = "";

    /// <summary>展示给用户的服务器名称（如 "国服"、"国际服"）。</summary>
    public string Name { get; set; } = "";

    /// <summary>渠道自定义选项（如库洛的 indexUrl、鹰角的 apiBase），由具体渠道实现解释。</summary>
    public Dictionary<string, string> Options { get; set; } = new();
}
