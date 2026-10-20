namespace WineParentBridge;

/// <summary>
/// helper 与 Core 侧共享的输入契约（2026-10-20 起由 C 迁到 C#）。
/// Core 侧常量在 <c>DecoyParentBridge</c>；测试直接读取本文件锚定两侧字面量一致，
/// 避免单方改名后 helper 退出 2、游戏不启动而 UI 仍报「已启动」。
/// </summary>
internal static class BridgeContract
{
    /// <summary>诱饵可执行文件路径（Games.exe 副本，必填）。</summary>
    public const string DecoyVariable = "YAGL_BRIDGE_DECOY";

    /// <summary>游戏可执行文件路径（lpApplicationName，必填）。</summary>
    public const string TargetVariable = "YAGL_BRIDGE_TARGET";

    /// <summary>游戏命令行（含引号的 exe 路径 + 参数；缺省回退为 TARGET）。</summary>
    public const string CommandLineVariable = "YAGL_BRIDGE_CMD";

    /// <summary>游戏工作目录（缺省继承 helper 进程）。</summary>
    public const string WorkingDirectoryVariable = "YAGL_BRIDGE_CWD";

    /// <summary>启动状态文件路径：helper 单独运行/自检时可省略；YAGL launcher 启动路径必须提供，
    /// 并以 READY 作为成功判据（状态写失败会导致 launcher 超时报错）。</summary>
    public const string StatusVariable = "YAGL_BRIDGE_STATUS";

    /// <summary>helper 已开始执行（游戏尚未创建）。</summary>
    public const string StartingStatus = "STARTING";

    /// <summary>helper 已成功创建游戏进程（此后 helper 透传的是游戏退出码，不再是桥失败）。</summary>
    public const string ReadyStatus = "READY";

    /// <summary>桥失败状态前缀，格式 <c>ERROR:&lt;exitCode&gt;:&lt;message&gt;</c>。</summary>
    public const string ErrorStatusPrefix = "ERROR:";

    /// <summary>必填输入缺失。</summary>
    public const int MissingInputExitCode = 2;

    /// <summary>诱饵创建/父进程属性设置失败。</summary>
    public const int DecoyFailureExitCode = 3;

    /// <summary>游戏进程创建失败。</summary>
    public const int GameCreateFailureExitCode = 4;
}
