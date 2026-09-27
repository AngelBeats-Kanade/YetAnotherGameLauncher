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

    /// <summary>
    /// umu 启动用的 UMU_ID 覆盖（形如 "umu-3513350"，需与 umu 数据库规范一致）。
    /// 留空时按 umu-{游戏id} 生成；prefix 路径不受此字段影响（仍按游戏 id 定位）。
    /// </summary>
    public string? UmuId { get; set; }

    /// <summary>
    /// 启用 Proton 的原生 Wayland 驱动（注入 PROTON_USE_WAYLAND=1，绕过 XWayland）。
    /// 仅 Linux Proton 启动链有意义；DW/GE/UMU-Proton 均识别该变量
    /// （DW-Proton 另接受 PROTON_ENABLE_WAYLAND 别名，两者映射同一 compat 选项）。
    /// </summary>
    public bool UseWayland { get; set; }

    /// <summary>
    /// 启动时把游戏内 DLSS 模型升级到 Proton 内置新版（注入 PROTON_DLSS_UPGRADE=1）。
    /// 依赖 DXVK-NVAPI，映射会连带注入 PROTON_ENABLE_NVAPI=1；仅 NVIDIA 显卡生效。
    /// </summary>
    public bool UpgradeDlss { get; set; }

    /// <summary>
    /// 打印 Proton 运行日志（注入 PROTON_LOG=1 与 PROTON_LOG_DIR=应用日志目录），
    /// 排查启动失败时与启动器自身的 launch-*.log 同目录可查。
    /// </summary>
    public bool EnableProtonLog { get; set; }

    /// <summary>附加环境变量（值同样支持 {installDir} 占位符）。</summary>
    public Dictionary<string, string> Environment { get; set; } = new();
}
