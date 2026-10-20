using System.Runtime.InteropServices;
using System.Text;

namespace WineParentBridge;

/// <summary>
/// 挂起诱饵父进程桥（Wine/Proton 与 Windows 原生直启共用的 Windows 侧编排器）。
/// 职责与输入契约见 BridgeContract.cs / tools/wine-parent-bridge/README.md。
/// </summary>
internal static class Program
{
    private const uint Infinite = 0xFFFFFFFF;

    /// <summary>helper 入口；<c>--write-parent &lt;file&gt;</c> 是自检模式（验证父进程归属）。</summary>
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && string.Equals(args[0], "--write-parent", StringComparison.Ordinal))
        {
            return WriteParentImage(args[1]);
        }

        return RunBridge();
    }

    /// <summary>执行桥接：挂起诱饵 → 以诱饵为父进程拉起游戏 → 等待/回收/透传退出码。</summary>
    private static int RunBridge()
    {
        var statusPath = Environment.GetEnvironmentVariable(BridgeContract.StatusVariable);
        TryWriteStatus(statusPath, BridgeContract.StartingStatus);

        var decoyPath = Environment.GetEnvironmentVariable(BridgeContract.DecoyVariable);
        var targetPath = Environment.GetEnvironmentVariable(BridgeContract.TargetVariable);
        if (string.IsNullOrWhiteSpace(decoyPath) || string.IsNullOrWhiteSpace(targetPath))
        {
            return Fail(
                "yagl-parent-bridge: missing YAGL_BRIDGE_DECOY/YAGL_BRIDGE_TARGET",
                BridgeContract.MissingInputExitCode,
                statusPath);
        }

        var commandLine = Environment.GetEnvironmentVariable(BridgeContract.CommandLineVariable);
        var workingDirectory = Environment.GetEnvironmentVariable(BridgeContract.WorkingDirectoryVariable);

        var decoy = default(NativeMethods.ProcessInformation);
        var decoyCreated = false;
        try
        {
            var startup = CreateStartupInfo();
            if (!NativeMethods.CreateProcessW(
                    decoyPath,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    NativeMethods.CreateSuspended,
                    IntPtr.Zero,
                    null,
                    ref startup,
                    out decoy))
            {
                return Fail(
                    WithWin32Error("yagl-parent-bridge: decoy CreateProcess failed"),
                    BridgeContract.DecoyFailureExitCode,
                    statusPath);
            }

            decoyCreated = true;
            return RunGame(targetPath, commandLine, workingDirectory, decoy, statusPath);
        }
        finally
        {
            if (decoyCreated)
            {
                NativeMethods.TerminateProcess(decoy.Process, 0);
                NativeMethods.CloseHandle(decoy.Thread);
                NativeMethods.CloseHandle(decoy.Process);
            }
        }
    }

    /// <summary>以诱饵句柄为 PROC_THREAD_ATTRIBUTE_PARENT_PROCESS 创建游戏并等待其退出。</summary>
    private static int RunGame(
        string targetPath,
        string? commandLine,
        string? workingDirectory,
        NativeMethods.ProcessInformation decoy,
        string? statusPath)
    {
        var attributeSize = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
        if (attributeSize == IntPtr.Zero)
        {
            return Fail(
                WithWin32Error("yagl-parent-bridge: attribute list sizing failed"),
                BridgeContract.DecoyFailureExitCode,
                statusPath);
        }

        var attributeList = Marshal.AllocHGlobal(attributeSize);
        // lpValue 指向的句柄变量必须活到 CreateProcessW 返回：实测 Wine 的
        // UpdateProcThreadAttribute 保存的是指针而非拷贝值，提前 FreeHGlobal 会让
        // 后续 CreateProcessW 读到已释放内存并以 ERROR_INVALID_HANDLE (6) 失败
        // （2026-10-20 复现；Windows 语义为拷贝，多占 8 字节无害）。详见 docs/PITFALLS.md。
        var parentHandleValue = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize))
            {
                return Fail(
                    WithWin32Error("yagl-parent-bridge: attribute list init failed"),
                    BridgeContract.DecoyFailureExitCode,
                    statusPath);
            }

            try
            {
                Marshal.WriteIntPtr(parentHandleValue, decoy.Process);
                if (!NativeMethods.UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        NativeMethods.ParentProcessAttribute,
                        parentHandleValue,
                        new IntPtr(IntPtr.Size),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    return Fail(
                        WithWin32Error("yagl-parent-bridge: parent attribute setup failed"),
                        BridgeContract.DecoyFailureExitCode,
                        statusPath);
                }

                var startup = CreateStartupInfo();
                startup.AttributeList = attributeList;
                var command = string.IsNullOrWhiteSpace(commandLine)
                    ? null
                    : new StringBuilder(commandLine);
                var currentDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? null
                    : workingDirectory;
                if (!NativeMethods.CreateProcessW(
                        targetPath,
                        command,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        true,
                        NativeMethods.ExtendedStartupInfoPresent,
                        IntPtr.Zero,
                        currentDirectory,
                        ref startup,
                        out var game))
                {
                    return Fail(
                        WithWin32Error("yagl-parent-bridge: game CreateProcess failed"),
                        BridgeContract.GameCreateFailureExitCode,
                        statusPath);
                }

                // 游戏进程已创建：此后 helper 等待并透传的是游戏自身退出码（例如校验拒绝的 5），
                // 不能再被 launcher 当作桥失败。READY 必须早于等待。
                TryWriteStatus(statusPath, BridgeContract.ReadyStatus);

                try
                {
                    NativeMethods.WaitForSingleObject(game.Process, Infinite);
                    if (!NativeMethods.GetExitCodeProcess(game.Process, out var exitCode))
                    {
                        exitCode = 1;
                    }

                    return unchecked((int)exitCode);
                }
                finally
                {
                    NativeMethods.CloseHandle(game.Thread);
                    NativeMethods.CloseHandle(game.Process);
                }
            }
            finally
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(parentHandleValue);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    /// <summary>STARTUPINFOEXW 的 cb 必须是扩展结构体大小（Wine 与 Windows 一致）。</summary>
    private static NativeMethods.StartupInfoEx CreateStartupInfo() =>
        new()
        {
            StartupInfo = new NativeMethods.StartupInfo
            {
                Size = Marshal.SizeOf<NativeMethods.StartupInfoEx>(),
            },
        };

    /// <summary>自检模式：把当前进程的父进程镜像路径写入文件。</summary>
    private static int WriteParentImage(string outputPath)
    {
        var path = GetParentImagePath();
        try
        {
            File.WriteAllText(outputPath, path ?? "<none>");
            return path is null ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 1;
        }
    }

    /// <summary>用 Toolhelp 快照找到当前进程的父 PID，再查询其镜像完整路径。</summary>
    private static string? GetParentImagePath()
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32CsSnapProcess, 0);
        if (snapshot == NativeMethods.InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var entry = new NativeMethods.ProcessEntry32
            {
                Size = Marshal.SizeOf<NativeMethods.ProcessEntry32>(),
            };
            if (!NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                return null;
            }

            var ownProcessId = Environment.ProcessId;
            var parentProcessId = 0;
            do
            {
                if (entry.ProcessId == ownProcessId)
                {
                    parentProcessId = entry.ParentProcessId;
                    break;
                }
            }
            while (NativeMethods.Process32NextW(snapshot, ref entry));

            if (parentProcessId == 0)
            {
                return null;
            }

            var parent = NativeMethods.OpenProcess(
                NativeMethods.ProcessQueryLimitedInformation, false, parentProcessId);
            if (parent == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var name = new StringBuilder(1024);
                var size = name.Capacity;
                return NativeMethods.QueryFullProcessImageNameW(parent, 0, name, ref size)
                    ? name.ToString(0, size)
                    : null;
            }
            finally
            {
                NativeMethods.CloseHandle(parent);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }

    /// <summary>给失败消息追加 GetLastError 值（诊断 Wine/Windows 差异）。</summary>
    private static string WithWin32Error(string message) =>
        $"{message} (Win32 error {Marshal.GetLastWin32Error()})";

    /// <summary>输出诊断并返回约定退出码（GUI 子系统下 .NET Console 可能是空设备，显式写 STDERR 句柄）。</summary>
    private static int Fail(string message, int exitCode, string? statusPath)
    {
        TryWriteStatus(statusPath, $"{BridgeContract.ErrorStatusPrefix}{exitCode}:{message}");
        WriteStandardError(message);
        return exitCode;
    }

    /// <summary>
    /// 原子写启动状态文件：临时文件 + rename，launcher 轮询时不会读到半个状态。
    /// 该文件是 YAGL launcher 的启动成功判据（READY）；写失败不阻断游戏启动，但 launcher 会在
    /// 超时后报错，而不是静默成功。helper 单独运行/自检可不设置 YAGL_BRIDGE_STATUS。
    /// </summary>
    private static void TryWriteStatus(string? statusPath, string value)
    {
        if (string.IsNullOrWhiteSpace(statusPath))
        {
            return;
        }

        try
        {
            var tempPath = statusPath + ".tmp";
            File.WriteAllText(tempPath, value);
            File.Move(tempPath, statusPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            // 写失败不阻断游戏启动；YAGL launcher 会按超时报错（不静默成功）
        }
    }

    /// <summary>优先写 Win32 STD_ERROR_HANDLE（SystemProcessRunner 重定向后仍可见），失败再退回 Console。</summary>
    private static void WriteStandardError(string message)
    {
        var handle = NativeMethods.GetStdHandle(-12);
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            var bytes = Encoding.UTF8.GetBytes(message + "\n");
            if (NativeMethods.WriteFile(handle, bytes, (uint)bytes.Length, out _, IntPtr.Zero))
            {
                return;
            }
        }

        Console.Error.WriteLine(message);
    }
}
