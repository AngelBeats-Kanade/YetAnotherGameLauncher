namespace YetAnotherGameLauncher.Core.Models;

/// <summary>游戏启动方式。支持占位符模板，游戏本体在 Linux 上如何运行（原生/wine/Proton）完全由配置决定。</summary>
public sealed class LaunchOptions
{
    /// <summary>
    /// 启动命令模板。可用占位符：{exe}（可执行文件完整路径）、{installDir}（游戏安装目录）。
    /// 例如 "{exe}"、'wine {exe}'、'steam -applaunch 000'。
    /// </summary>
    public string CommandTemplate { get; set; } = "{exe}";

    /// <summary>工作目录模板，可用 {installDir} 占位符；留空时使用 {installDir}。</summary>
    public string WorkingDirectory { get; set; } = "{installDir}";

    /// <summary>附加环境变量（值同样支持 {installDir} 占位符）。</summary>
    public Dictionary<string, string> Environment { get; set; } = new();
}
