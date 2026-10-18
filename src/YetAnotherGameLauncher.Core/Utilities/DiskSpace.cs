namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>
/// 磁盘空闲空间探测（更新前置预检用，2026-10-18）。按"最长挂载点前缀"匹配路径所在的卷：
/// Windows 是盘符根（<c>C:\</c>），Linux 是 /proc/mounts 里的挂载点（<c>/</c>、<c>/home</c>…），
/// 因此不能把安装目录直接喂给 <see cref="DriveInfo"/>（它只接受卷根）。
/// 探测不出（平台/权限/奇怪的挂载表）一律返回 null = 未知——调用方必须跳过预检而不是拦更新。
/// </summary>
public static class DiskSpace
{
    /// <summary>
    /// 测试缝：非 null 时直接返回其值（压小以验证空间不足路径）。设置它的测试必须与其它
    /// 使用该缝的测试同处 sequential 集合（并行窗口会串扰）。
    /// </summary>
    internal static Func<string, long?>? AvailableFreeBytesForTests;

    /// <summary>路径所在卷的空闲字节数；探测不出返回 null（调用方跳过预检）。</summary>
    public static long? TryGetAvailableFreeBytes(string path) =>
        AvailableFreeBytesForTests?.Invoke(path) ?? ProbeAgainstMountedDrives(path, EnumerateMountedDrives());

    /// <summary>
    /// 纯匹配逻辑（可直测）：取路径的最长挂载点前缀对应的空闲字节；无匹配返回 null。
    /// 前缀必须落在目录边界上（<c>/homex</c> 不得算进 <c>/home</c>）；Windows 盘符不区分大小写。
    /// </summary>
    /// <param name="path">待探测路径。</param>
    /// <param name="drives">已挂载卷（根 + 空闲字节）。</param>
    /// <param name="windows">null = 按当前平台；测试用于覆盖另一平台的匹配规则。</param>
    internal static long? ProbeAgainstMountedDrives(
        string path, IReadOnlyList<(string Root, long Free)> drives, bool? windows = null)
    {
        if (string.IsNullOrWhiteSpace(path) || drives.Count == 0)
        {
            return null;
        }

        var isWindows = windows ?? OperatingSystem.IsWindows();
        var comparison = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // 只在"按当前平台调用"时做真实路径归一：Path.GetFullPath 对异平台路径语义不对
        // （Linux 上把 "D:\Games\x" 当含反斜杠的普通文件名、还会拼上当前目录）。
        // Windows 形态统一分隔符后比较（Windows 两种分隔符都接受），测试即可在任一平台覆盖两条规则。
        var fullPath = isWindows == OperatingSystem.IsWindows()
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            : path;
        if (isWindows)
        {
            fullPath = fullPath.Replace('\\', '/');
        }

        long? best = null;
        var bestLength = -1;
        foreach (var (root, free) in drives)
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
            if (normalizedRoot.Length == 0)
            {
                continue; // 空根会匹配一切，丢弃（防御）
            }

            if (isWindows)
            {
                normalizedRoot = normalizedRoot.Replace('\\', '/');
            }

            var separator = isWindows ? '/' : Path.DirectorySeparatorChar;
            var isDriveRoot = isWindows && normalizedRoot.Length == 2 && normalizedRoot[1] == ':';
            var matches = fullPath.StartsWith(normalizedRoot, comparison)
                && (isDriveRoot
                    || fullPath.Length == normalizedRoot.Length
                    || normalizedRoot.EndsWith(separator)
                    || fullPath[normalizedRoot.Length] == separator);
            if (matches && normalizedRoot.Length > bestLength)
            {
                best = free;
                bestLength = normalizedRoot.Length;
            }
        }

        return best;
    }

    /// <summary>枚举已挂载的卷（读不到空闲值的卷跳过；整套失败返回空表 = 未知）。</summary>
    private static IReadOnlyList<(string Root, long Free)> EnumerateMountedDrives()
    {
        try
        {
            var drives = new List<(string, long)>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady)
                    {
                        drives.Add((drive.RootDirectory.FullName, drive.AvailableFreeSpace));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // 单个卷读不到（权限/伪文件系统）：跳过该卷，其余照常
                }
            }

            return drives;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }
}
