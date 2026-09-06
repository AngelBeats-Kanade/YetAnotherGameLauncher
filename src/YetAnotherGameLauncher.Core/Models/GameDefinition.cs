namespace YetAnotherGameLauncher.Core.Models;

/// <summary>一个游戏在配置文件中的完整定义。游戏列表完全由 games.json 驱动，代码不内置任何具体游戏。</summary>
public sealed class GameDefinition
{
    /// <summary>唯一标识（kebab-case），例如 "wuthering-waves"。</summary>
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>
    /// 显示名的本地化映射（culture 名 → 显示名），如 {"zh-CN": "鸣潮", "en-US": "Wuthering Waves"}。
    /// 缺失或无对应 culture 时回退 DisplayName。
    /// </summary>
    public Dictionary<string, string> NameLocalized { get; set; } = [];

    /// <summary>图标路径，相对启动器资源目录或绝对路径。</summary>
    public string Icon { get; set; } = "";

    /// <summary>渠道实现键（如 "kuro"、"hypergryph"），对应 DI 中注册的 IGameChannelApi。</summary>
    public string Channel { get; set; } = "";

    /// <summary>可选的多个服务器（国服/国际服等）。</summary>
    public List<GameServer> Servers { get; set; } = [];

    /// <summary>安装目录：相对 settings.installRoot，也可以是绝对路径。</summary>
    public string InstallDir { get; set; } = "";

    /// <summary>游戏可执行文件，相对安装目录。</summary>
    public string Executable { get; set; } = "";

    public LaunchOptions Launch { get; set; } = new();
}
