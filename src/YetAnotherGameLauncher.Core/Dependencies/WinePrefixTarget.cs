namespace YetAnotherGameLauncher.Core.Dependencies;

/// <summary>一次依赖安装的目标位置（由 App 层按启动方式解析；Core 只消费结果）。</summary>
/// <param name="WineExecutable">用于注册表导入的 wine 可执行文件绝对路径。</param>
/// <param name="WinePrefixDirectory">WINEPREFIX 环境变量取值（umu 布局下是 &lt;compatdata&gt;/pfx）。</param>
/// <param name="FontsDirectory">字体落位目录（&lt;prefix&gt;/drive_c/windows/Fonts）。</param>
/// <param name="StateDirectory">安装状态目录（&lt;prefix 根&gt;/.yagl-deps；完成标记与临时 .reg 落此，
/// 随 prefix 一起删除即自动重置）。</param>
public sealed record WinePrefixTarget(
    string WineExecutable,
    string WinePrefixDirectory,
    string FontsDirectory,
    string StateDirectory);

/// <summary>依赖安装的失败分类（UI 按类给可操作文案）。</summary>
public enum DependencyFailureKind
{
    /// <summary>找不到可用的 wine 可执行文件。</summary>
    WineMissing,

    /// <summary>Wine prefix 未初始化（drive_c 不存在，需先启动一次游戏）。</summary>
    PrefixMissing,

    /// <summary>Wine prefix 的内置组件链接失效（Proton 兼容组件升级删除旧目录的遗留），
    /// 自动修复后仍有残留——wine 在该 prefix 内无法启动任何 PE 进程。</summary>
    PrefixUnhealthy,

    /// <summary>压缩包下载失败（含校验不过）。</summary>
    DownloadFailed,

    /// <summary>解压失败或清单声明的字体条目缺失。</summary>
    ExtractFailed,

    /// <summary>字体拷贝进 prefix 失败。</summary>
    FontCopyFailed,

    /// <summary>wine reg import 非零退出、启动失败或注册表脚本写盘失败。</summary>
    RegistryFailed,

    /// <summary>完成标记写入失败（字体与注册表已生效，但安装状态未被记录）。</summary>
    StateWriteFailed,
}

/// <summary>依赖安装失败（携带分类，消息面向日志；用户文案由 UI 按分类本地化）。</summary>
public sealed class DependencyException(DependencyFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public DependencyFailureKind Kind { get; } = kind;
}

/// <summary>安装进度阶段（UI 映射为本地化文本；Fraction 仅下载阶段有值，∈[0,1]）。</summary>
public enum DependencyPhase
{
    Downloading,
    Extracting,
    Copying,
    Registering,
    Done,
}

/// <summary>安装进度报告。</summary>
/// <param name="Phase">当前阶段。</param>
/// <param name="Fraction">阶段内进度（null = 不定）。</param>
public sealed record DependencyProgress(DependencyPhase Phase, double? Fraction);

/// <summary>依赖安装状态（读完成标记得出）。</summary>
public sealed record DependencyInstallState(bool Installed, string? InstalledVersion);
