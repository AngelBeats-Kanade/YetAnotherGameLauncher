using System.Runtime.InteropServices;
using System.Text;

namespace WineParentBridge;

/// <summary>helper 需要的 Win32 API 与结构体（仅 x64 Windows；Wine 提供同名实现）。</summary>
internal static class NativeMethods
{
    /// <summary>CREATE_SUSPENDED：诱饵主线程永不恢复。</summary>
    public const uint CreateSuspended = 0x00000004;

    /// <summary>EXTENDED_STARTUPINFO_PRESENT：让 CreateProcessW 读取父进程属性。</summary>
    public const uint ExtendedStartupInfoPresent = 0x00080000;

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION（读取父进程镜像路径所需的最小权限）。</summary>
    public const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>TH32CS_SNAPPROCESS。</summary>
    public const uint Th32CsSnapProcess = 0x00000002;

    /// <summary>PROC_THREAD_ATTRIBUTE_PARENT_PROCESS。</summary>
    public static readonly IntPtr ParentProcessAttribute = new(0x00020000);

    /// <summary>INVALID_HANDLE_VALUE。</summary>
    public static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>STARTUPINFOW。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        /// <summary>结构体字节数，调用前必须赋值。</summary>
        public int Size;

        /// <summary>保留字段。</summary>
        public IntPtr Reserved;

        /// <summary>桌面名称。</summary>
        public IntPtr Desktop;

        /// <summary>窗口标题。</summary>
        public IntPtr Title;

        /// <summary>窗口 X 坐标。</summary>
        public int X;

        /// <summary>窗口 Y 坐标。</summary>
        public int Y;

        /// <summary>窗口宽度。</summary>
        public int XSize;

        /// <summary>窗口高度。</summary>
        public int YSize;

        /// <summary>控制台缓冲区列数。</summary>
        public int XCountChars;

        /// <summary>控制台缓冲区行数。</summary>
        public int YCountChars;

        /// <summary>控制台填充属性。</summary>
        public int FillAttribute;

        /// <summary>启动标志。</summary>
        public int Flags;

        /// <summary>显示方式。</summary>
        public short ShowWindow;

        /// <summary>保留字段。</summary>
        public short ReservedSize;

        /// <summary>保留字段。</summary>
        public IntPtr ReservedPointer;

        /// <summary>标准输入句柄。</summary>
        public IntPtr StandardInput;

        /// <summary>标准输出句柄。</summary>
        public IntPtr StandardOutput;

        /// <summary>标准错误句柄。</summary>
        public IntPtr StandardError;
    }

    /// <summary>STARTUPINFOEXW：STARTUPINFOW + 属性列表指针。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfoEx
    {
        /// <summary>基础启动信息；<c>Size</c> 必须是本结构体的字节数。</summary>
        public StartupInfo StartupInfo;

        /// <summary>PROC_THREAD_ATTRIBUTE_LIST 指针。</summary>
        public IntPtr AttributeList;
    }

    /// <summary>PROCESS_INFORMATION。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        /// <summary>进程句柄。</summary>
        public IntPtr Process;

        /// <summary>主线程句柄。</summary>
        public IntPtr Thread;

        /// <summary>进程 ID。</summary>
        public int ProcessId;

        /// <summary>主线程 ID。</summary>
        public int ThreadId;
    }

    /// <summary>PROCESSENTRY32W。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ProcessEntry32
    {
        /// <summary>结构体字节数，调用前必须赋值。</summary>
        public int Size;

        /// <summary>引用计数。</summary>
        public int Usage;

        /// <summary>进程 ID。</summary>
        public int ProcessId;

        /// <summary>默认堆 ID。</summary>
        public IntPtr DefaultHeapId;

        /// <summary>模块 ID。</summary>
        public int ModuleId;

        /// <summary>线程数。</summary>
        public int ThreadCount;

        /// <summary>父进程 ID。</summary>
        public int ParentProcessId;

        /// <summary>基础优先级。</summary>
        public int PriorityClassBase;

        /// <summary>标志。</summary>
        public int Flags;

        /// <summary>可执行文件名（MAX_PATH）。</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    /// <summary>创建进程。</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder? commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    /// <summary>初始化属性列表；首次传 null 句柄可探测所需字节数。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref IntPtr size);

    /// <summary>写入父进程属性。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    /// <summary>释放属性列表。</summary>
    [DllImport("kernel32.dll")]
    public static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    /// <summary>结束进程。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    /// <summary>读取进程退出码。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    /// <summary>等待进程结束。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    /// <summary>关闭句柄。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    /// <summary>创建进程快照。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    /// <summary>枚举第一个进程。</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    /// <summary>枚举下一个进程。</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    /// <summary>打开进程。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    /// <summary>读取进程镜像完整路径。</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(
        IntPtr process,
        int flags,
        StringBuilder executableName,
        ref int size);

    /// <summary>取标准句柄（STD_ERROR_HANDLE = -12）。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(int standardHandle);

    /// <summary>直接写句柄：GUI 子系统进程的 .NET Console 可能是空设备，退出前诊断必须显式写出。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteFile(
        IntPtr file,
        byte[] buffer,
        uint numberOfBytesToWrite,
        out uint numberOfBytesWritten,
        IntPtr overlapped);
}
