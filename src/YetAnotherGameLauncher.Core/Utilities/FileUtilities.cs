namespace YetAnotherGameLauncher.Core.Utilities;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

/// <summary>文件工具：原子写入、静默删除、文件名清洗与可执行位检查。</summary>
public static class FileUtilities
{
    /// <summary>
    /// 目标路径的硬链接数（同一 inode 的目录项数；F43）。.NET 无可移植 API：
    /// Linux x64 经 stat(2) 的 st_nlink，Windows 经 GetFileInformationByHandle 的
    /// nNumberOfLinks。探测失败（含 glibc 低于 2.33 的 EntryPointNotFound 与 libc 不可
    /// 加载的 DllNotFound——2.33 起才导出 stat 动态符号，此前是 __xstat 内联包装）、
    /// 路径不存在、其他平台/架构（aarch64 的 stat 布局不同，未实现）一律返回 1——
    /// 按"非硬链接"放行，与 <see cref="IsReparsePoint"/> 的失败语义一致（后续写入自会
    /// 暴露真实问题）。
    /// </summary>
    internal static long HardLinkCount(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var handle = File.OpenHandle(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return GetFileInformationByHandle(handle, out var info) ? info.NumberOfLinks : 1;
            }

            if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                return stat(path, out var st) == 0 ? unchecked((long)st.st_nlink) : 1;
            }

            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            return 1;
        }
    }

    /// <summary>Linux x64 的 struct stat（内核 ABI 布局，共 144 字节—— marshaling 缓冲必须
    /// 等大，声明不足会被 stat(2) 越界写）。填充字段按平台 ABI 占位，无语义。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StatLinuxX64
    {
        public ulong st_dev;
        public ulong st_ino;
        public ulong st_nlink;
        public uint st_mode;
        public uint st_uid;
        public uint st_gid;
        public readonly uint Pad0;
        public ulong st_rdev;
        public ulong st_size;
        public ulong st_blksize;
        public ulong st_blocks;
        public ulong st_atime;
        public ulong st_atime_nsec;
        public ulong st_mtime;
        public ulong st_mtime_nsec;
        public ulong st_ctime;
        public ulong st_ctime_nsec;
        public readonly ulong Unused0;
        public readonly ulong Unused1;
        public readonly ulong Unused2;
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "stat")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int stat(string pathname, out StatLinuxX64 statbuf);

    /// <summary>BY_HANDLE_FILE_INFORMATION（FILETIME 按 4 字节对齐展开成两个 uint，
    /// NumberOfLinks 落在偏移 40——用 ulong 字段会被 8 字节对齐插出错误布局）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out ByHandleFileInformation information);
    /// <summary>原子写入文本：先写同目录 .tmp 再 Move 覆盖，避免写入中途崩溃损坏目标文件。
    /// 失败路径不留 .tmp 残留（F68）：写出与落盘任一环失败都清理临时文件后再上抛。</summary>
    public static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var tempPath = PrepareTempPath(path);
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            MoveOverwriteWithRetry(tempPath, path, cancellationToken);
        }
        finally
        {
            // 任一环失败（写出抛出/Move 重试耗尽/取消）都不留 .tmp 残留
            DeleteQuiet(tempPath);
        }
    }

    /// <summary>同步原子写入：仅供无法 await 的关闭路径；普通异步调用方一律用
    /// <see cref="WriteAtomicAsync"/>。</summary>
    public static void WriteAtomic(string path, string content)
    {
        var tempPath = PrepareTempPath(path);
        try
        {
            File.WriteAllText(tempPath, content);
            MoveOverwriteWithRetry(tempPath, path, CancellationToken.None);
        }
        finally
        {
            DeleteQuiet(tempPath);
        }
    }

    /// <summary>建父目录并返回同目录临时文件路径。</summary>
    private static string PrepareTempPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return path + ".tmp";
    }

    /// <summary>Move 覆盖 + Windows 只读/占用重试一次；失败包装为可操作的 IOException。
    /// 同步与异步原子写共用，保证两条落盘路径语义一致。</summary>
    private static void MoveOverwriteWithRetry(string tempPath, string path, CancellationToken cancellationToken)
    {
        // Windows 语义坑：目标被占用（编辑器/杀软）或带只读属性时 Move 覆盖会抛
        // IOException/UnauthorizedAccessException，而 Linux 的 rename() 总能成功。
        // 只读是常见原因，就地解除后重试一次；仍失败则把真实原因包进可操作的错误里。
        // 目标不存在的首写形态同样要走重试链（旧过滤器要求 File.Exists(path)，
        // 把首写失败漏成裸异常，F68）
        try
        {
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   && !cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }

            try
            {
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception retry) when (retry is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"无法写入 {path}，文件可能正被其它程序占用：{retry.Message}", retry);
            }
        }
    }

    /// <summary>尽力删除文件：占用/权限等失败静默忽略，仅用于清理场景。</summary>
    public static void DeleteQuiet(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 落笔前的叶子复核（F19/F43 同族，2026-10-17 复审）：目标名若已被符号链接（含悬空形态）
    /// 或硬链接占位，直接写会跟随链接/截断共享 inode——写到沙箱之外。临时名
    /// （*.yagl-delta/*.yagl-new）在不可信清单的命名空间里，必须当成外部数据对待。
    /// 命中即删掉这个目录项（删链接本身，不动其指向与同 inode 的其它名），随后写入全新文件。
    /// </summary>
    internal static void RemoveLinkedLeaf(string path, ILogger? logger)
    {
        if (IsReparsePoint(path))
        {
            logger?.LogWarning("Removing a pre-existing link before writing: {Path}", path);
            DeleteQuiet(path);
            EnsureLeafUnlinked(path);
            return;
        }

        if (File.Exists(path) && HardLinkCount(path) > 1)
        {
            logger?.LogWarning("Removing a pre-existing hard-linked file before writing: {Path}", path);
            DeleteQuiet(path);
            EnsureLeafUnlinked(path);
        }
    }

    /// <summary>
    /// 解除后复查（复审 nit，2026-10-17）：DeleteQuiet 吞掉失败后若继续写入，就绕过了这道新防线
    /// （粘滞目录 + 他人所有的链接等极端形态）。仍在链接占位即抛 IOException——宁可响亮失败，
    /// 也不能让写入跟随链接落到沙箱之外。
    /// </summary>
    private static void EnsureLeafUnlinked(string path)
    {
        if (IsReparsePoint(path) || (File.Exists(path) && HardLinkCount(path) > 1))
        {
            throw new IOException($"Unable to remove a pre-existing link before writing: {path}");
        }
    }

    /// <summary>路径是否为符号链接/junction 等重解析点；探测失败按否处理（后续删除自会再兜）。
    /// FNFE/DNFE 均是 IOException 子类（官方继承链，2026-09-25 探针复核），"路径不存在"天然被
    /// IOException 臂覆盖——catch 真正防的是 UOE（权限）与竞态 IO 错误；不存在 = 非链接。</summary>
    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>只删除链接本身而不进目标子树：先按目录链接删，失败再按文件链接删。</summary>
    private static bool TryDeleteLinkQuiet(string path, ILogger? logger)
    {
        try
        {
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogDebug(ex, "Skipped reparse point during tree delete: {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// 尽力删除目录树：逐条目删除后自底向上删子目录，返回目录是否已完全删除。
    /// Windows 上单个被占用/只读的文件会让 <see cref="Directory.Delete(string,bool)"/> 整体抛异常，
    /// 进而打断更新/清理链（Linux 上打开中的文件照样可删，问题只在 Windows 暴露）；
    /// 这里能删多少删多少，删不掉的残留留给下次重试或事后修复。
    /// </summary>
    public static bool TryDeleteDirectory(string path, ILogger? logger = null)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        var clean = true;
        try
        {
            // IgnoreInaccessible：子树含无 ListDirectory 权限的目录时跳过该子项（官方语义），
            // 其余照删——单不可读子目录不得打断整链（F25；path 自身不可读时枚举仍抛，
            // 由外层 catch 兜成 false 保住 best-effort 契约）。
            // AttributesToSkip 必须显式归零：默认 Hidden|System 会在 Linux 上把点前缀条目
            // （.installed.ok/.yagl-* 等，.NET 标记为 Hidden）从枚举剔除——删不净即回归
            //（复审 R1 探针实锤：默认 1 条 vs 完整 2 条）
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                path, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.None,
                }))
            {
                // 符号链接/junction 只删链接本身：递归会穿过链接把目标处（可能远在自管目录之外）
                // 的真实文件删掉。先按目录链接删，失败（实为文件链接）再按文件删。
                if (IsReparsePoint(entry))
                {
                    if (!TryDeleteLinkQuiet(entry, logger))
                    {
                        clean = false;
                    }

                    continue;
                }

                if (Directory.Exists(entry) && !File.Exists(entry))
                {
                    clean &= TryDeleteDirectory(entry, logger);
                }
                else
                {
                    DeleteQuiet(entry);
                    if (File.Exists(entry))
                    {
                        logger?.LogDebug("Skipped in-use file during tree delete: {Path}", entry);
                        clean = false;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogDebug(ex, "Cannot enumerate directory tree, left in place: {Path}", path);
            return false;
        }

        try
        {
            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogDebug(ex, "Directory still in use, left in place: {Path}", path);
            return false;
        }

        return clean;
    }

    /// <summary>
    /// 确保落点的父目录链（沙箱根之下）不含既有符号链接/reparse 点（F19）：不可信清单/载荷的
    /// 产物落点若穿过链接，写出/删除都会落到沙箱之外。命中即抛 IOException 由调用方折算。
    /// 解压（PackageInstallerService）与增量载荷落位（DeltaPackageApplier）共用。
    /// </summary>
    internal static void EnsureNoReparseWithin(string rootDirectory, string target)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var fullTarget = Path.GetFullPath(target);
        if (!fullTarget.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(fullTarget, root, StringComparison.Ordinal))
        {
            throw new IOException($"Archive target escapes sandbox: {target}");
        }

        var current = Path.GetDirectoryName(fullTarget);
        while (current is not null
               && !string.Equals(current, root, StringComparison.Ordinal)
               && current.Length > root.Length)
        {
            if (Directory.Exists(current) && IsReparsePoint(current))
            {
                throw new IOException($"Archive target traverses a symlinked directory: {current}");
            }

            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>gameId 只保留文件名安全字符，其余替换为 '-'；清洗后为空返回 "game"（启动日志等文件名用）。</summary>
    public static string SanitizeGameId(string gameId)
    {
        var sanitized = new string(gameId.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return sanitized.Length == 0 ? "game" : sanitized;
    }

    /// <summary>
    /// 启动日志文件路径：{logDir}/launch-{gameId}-{yyyyMMdd-HHmmss}-{4位随机}.log。
    /// 时间戳精确到秒，同秒内对同一游戏的两次启动会互相覆盖日志，追加短随机后缀保证唯一。
    /// GameLauncherService 与 NativeUmuLauncher 共用，保持日志命名单一来源。
    /// </summary>
    public static string LaunchLogFilePath(string logDirectory, string gameId) => Path.Combine(
        logDirectory,
        $"launch-{SanitizeGameId(gameId)}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}.log");

    /// <summary>
    /// 应用级日志文件路径：{logDir}/yagl-yyyyMMdd.log。FileLoggerProvider 与启动超时提示共用，
    /// 避免文件命名格式两处维护（2026-10-22 复审：边界诊断写在应用日志里，提示必须指向它）。
    /// </summary>
    public static string ApplicationLogFilePath(string logDirectory, DateOnly date) => Path.Combine(
        logDirectory, $"yagl-{date:yyyyMMdd}.log");

    /// <summary>文件是否存在且可执行：Linux 校验 UserExecute 位；Windows 无执行位概念，存在即可。</summary>
    public static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
