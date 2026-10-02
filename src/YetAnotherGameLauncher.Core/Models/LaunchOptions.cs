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

    /// <summary>
    /// 鸣潮资源包档位（2026-10-02）：hd / sd / uhd 三档之一，启动时以 -krqlv=&lt;tier&gt;
    /// 命令行参数传给游戏（官方启动器同款参数）；null/空 = 跟随游戏内设置、不追加参数。
    /// 渠道语义字段，非鸣潮游戏忽略；白名单校验见 GameCatalogService（拒绝大写与未知值）。
    /// </summary>
    public string? ResourceQualityTier { get; set; }

    /// <summary>附加环境变量（值同样支持 {installDir} 占位符）。</summary>
    public Dictionary<string, string> Environment { get; set; } = new();

    /// <summary>
    /// 自定义启动选项中显式 %command% 之后的游戏命令行参数（已去引号的 token 列表）。
    /// 启动时按序追加在游戏可执行文件之后、鸣潮 -krqlv 档位参数之前；token 支持
    /// {exe}/{installDir} 占位符（与环境变量值同规则）。null/空 = 无追加参数；
    /// 框内未写 %command% 时语义上等价于占位符自动补在末尾（Steam 启动选项格式，
    /// 2026-10-03）：KEY=VALUE 条目照常注入环境变量，本字段只承载占位符之后的部分。
    /// </summary>
    public List<string>? Arguments { get; set; }
}
