namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>启动失败的类目（决定 UI 的友好提示与修复指引）。</summary>
public enum LaunchFailureKind
{
    /// <summary>游戏主程序文件不存在。</summary>
    ExecutableMissing,

    /// <summary>启动命令（wine/umu-run/自定义模板首段）在系统上找不到。</summary>
    RuntimeMissing,

    /// <summary>启动命令存在但没有可执行权限，且自动补授权失败。</summary>
    RuntimeNotExecutable,

    /// <summary>Wine prefix 目录无法创建（磁盘/权限问题）。</summary>
    PrefixCreateFailed,

    /// <summary>进程启动调用本身失败（权限/格式/资源等）。</summary>
    StartFailed,

    /// <summary>其它未分类失败。</summary>
    Unknown,
}

/// <summary>
/// 启动预检/启动过程的类目化失败：message 已中文化、可直接向用户展示；
/// Kind 供 UI 决定修复指引（如"去引导安装 umu-launcher"）。
/// </summary>
public sealed class LaunchException(LaunchFailureKind kind, string message, Exception? inner = null)
    : UpdateException(message, inner)
{
    /// <summary>失败类目。</summary>
    public LaunchFailureKind Kind { get; } = kind;
}
