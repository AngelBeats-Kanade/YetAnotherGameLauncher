namespace YetAnotherGameLauncher.Core.Dependencies;

/// <summary>备份前的目标形态（决定卸载/回滚时如何还原）。</summary>
internal enum DependencyBackupKind
{
    /// <summary>原始是符号链接（Proton 管理的 prefix）：记录 target，还原时重建链接，绝不写穿。</summary>
    Symlink,

    /// <summary>原始是真实文件：复制到备份目录并记录 SHA-256，还原时复制回去。</summary>
    File,

    /// <summary>原始不存在：回滚/卸载时删除新文件。</summary>
    Missing,
}

/// <summary>单个 DLL 的备份记录。</summary>
/// <param name="Arch">x64 / x86。</param>
/// <param name="FileName">DLL 文件名。</param>
/// <param name="Kind">原始形态。</param>
/// <param name="LinkTarget">原始符号链接目标（Kind=Symlink 时有值）。</param>
/// <param name="BackupFileName">备份副本文件名（Kind=File 时有值，位于备份目录内）。</param>
/// <param name="Sha256">原始文件 SHA-256（Kind=File 时有值）。</param>
internal sealed record DependencyDllBackup(
    string Arch,
    string FileName,
    DependencyBackupKind Kind,
    string? LinkTarget,
    string? BackupFileName,
    string? Sha256);

/// <summary>单个 DllOverrides 键的旧值记录。</summary>
/// <param name="Key">注册表值名。</param>
/// <param name="OldValue">安装前的值；null = 原本无值（卸载时删除）。</param>
internal sealed record DependencyOverrideBackup(string Key, string? OldValue);

/// <summary>winetricks 来源依赖的备份清单（原子写入备份目录内 manifest.json）。</summary>
/// <param name="Id">依赖 id。</param>
/// <param name="Version">安装时的依赖版本。</param>
/// <param name="Dlls">每个 DLL 的原始形态。</param>
/// <param name="Overrides">每个覆盖键的旧值。</param>
internal sealed record DependencyBackupManifest(
    string Id,
    string Version,
    IReadOnlyList<DependencyDllBackup> Dlls,
    IReadOnlyList<DependencyOverrideBackup> Overrides);
