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

    /// <summary>prefix 架构不是 win64（win32 prefix 没有 syswow64，x64 DLL 条目也不成立）。</summary>
    PrefixUnsupported,

    /// <summary>prefix 正被 Wine 进程占用（游戏运行中）；任何写操作前拒绝。</summary>
    PrefixBusy,

    /// <summary>找不到 winetricks（Proton 自带与 PATH 都没有）。</summary>
    WinetricksMissing,

    /// <summary>winetricks 非零退出或启动失败。</summary>
    WinetricksFailed,

    /// <summary>备份现有 DLL/注册表覆盖失败（未做任何修改）。</summary>
    BackupFailed,

    /// <summary>安装标记存在但备份缺失/损坏，或备份存在但清单不可用——无法自动还原。</summary>
    BackupMissing,

    /// <summary>上次安装中断后的自动恢复失败，或存在备份且标记完整时禁止重新备份。</summary>
    RecoveryFailed,

    /// <summary>失败后的回滚未完成（备份与日志已保留）。</summary>
    RollbackFailed,

    /// <summary>winetricks 返回后 DLL 仍是指向 Proton 的符号链接、尺寸或 SHA-256 不符。</summary>
    DllVerifyFailed,

    /// <summary>写入 HKCU\Software\Wine\DllOverrides 失败。</summary>
    OverrideFailed,

    /// <summary>卸载还原 DLL/覆盖失败（备份与标记保留，可重试）。</summary>
    RestoreFailed,

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

    /// <summary>winetricks 正在安装原生组件（含其自身下载，进度不定）。</summary>
    Installing,
    Done,
}

/// <summary>安装进度报告。</summary>
/// <param name="Phase">当前阶段。</param>
/// <param name="Fraction">阶段内进度（null = 不定）。</param>
public sealed record DependencyProgress(DependencyPhase Phase, double? Fraction);

/// <summary>依赖状态五态（UI 据此区分安装/修复/还原/手动恢复）。</summary>
public enum DependencyStateKind
{
    /// <summary>未安装（无标记、无有效备份；仅“备份哨兵存在且 DLL/覆盖仍为原状”的预提交残留归一为此态）。</summary>
    NotInstalled,

    /// <summary>已安装（marker/文件/备份校验通过；覆盖值查询不可用时带
    /// <see cref="DependencyInstallState.VerificationUnavailable"/>，仍属本态）。</summary>
    Installed,

    /// <summary>标记在但任一校验（DLL/覆盖）不满足；可自动修复（卸载→安装）。</summary>
    Damaged,

    /// <summary>备份缺失/损坏或标记残留；无法自动还原，只能手动恢复或清理残留标记。</summary>
    BackupMissing,

    /// <summary>标记缺失但备份有效（上次安装中断）；可「恢复并安装」。</summary>
    NeedsRecovery,
}

/// <summary>依赖安装状态。</summary>
/// <param name="Kind">五态之一。</param>
/// <param name="InstalledVersion">已安装版本（仅 <see cref="DependencyStateKind.Installed"/> 有值）。</param>
/// <param name="VerificationUnavailable">已安装但覆盖值查询超时/失败，状态暂无法完全确认
/// （UI 提示刷新重试，不得按 <see cref="DependencyStateKind.Damaged"/> 引导重装）。</param>
public sealed record DependencyInstallState(
    DependencyStateKind Kind,
    string? InstalledVersion,
    bool VerificationUnavailable = false)
{
    /// <summary>是否处于已安装态（UI 绿点/重装按钮沿用此判据；覆盖值校验不可用时仍为 true）。</summary>
    public bool Installed => Kind == DependencyStateKind.Installed;

    /// <summary>未安装（无标记、无有效备份；仅当备份目录含 `.incomplete` 且当前 DLL/覆盖仍为原状时，
    /// 预提交残留也归入此态）。</summary>
    public static DependencyInstallState NotInstalled { get; } =
        new(DependencyStateKind.NotInstalled, null);
}
