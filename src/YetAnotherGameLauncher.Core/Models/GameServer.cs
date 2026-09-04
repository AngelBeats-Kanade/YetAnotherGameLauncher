namespace YetAnotherGameLauncher.Core.Models;

/// <summary>游戏目录中的一个服务器/渠道入口（如国服、国际服）。</summary>
public sealed class GameServer
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>渠道自定义选项（如库洛的 indexUrl、鹰角的 apiBase），由具体渠道实现解释。</summary>
    public Dictionary<string, string> Options { get; set; } = new();
}
