/*
 * yagl-parent-bridge —— 挂起诱饵父进程桥（Wine/Proton 链的 Windows 侧编排器）。
 *
 * 背景（社区方案等价实现，参考 misaka10843/Hi3Helper.Plugin.Hypergryph 的
 * DECOY_PARENT_BRIDGE 与 Dr-hydra/Better-Endfield 的官方启动器父进程桥）：
 * 终末地 1.6.3+ 启动时校验"直接父进程"——要求父进程镜像文件名匹配、其 Authenticode
 * 签名可验证、签名者证书指纹在白名单内（实现于游戏目录的 OneChannelLC.dll）。
 * 本桥把游戏目录里鹰角签名的可执行文件副本（由 YAGL 复制为 Games.exe）以挂起状态
 * 作为游戏的父进程，从而通过校验且不启动任何官方启动器组件。
 *
 * 职责：
 *   1. 以 CREATE_SUSPENDED 创建诱饵进程，主线程永不恢复（诱饵自身的代码不得运行）；
 *   2. 以诱饵进程句柄为 PROC_THREAD_ATTRIBUTE_PARENT_PROCESS 创建游戏进程；
 *   3. 等待游戏退出 → 结束诱饵进程 → 以游戏退出码退出。
 *
 * 输入（环境变量，由 YAGL 注入，全部为 Windows 路径）：
 *   YAGL_BRIDGE_DECOY   诱饵可执行文件路径（Games.exe 副本，必填）
 *   YAGL_BRIDGE_TARGET  游戏可执行文件路径（lpApplicationName，必填）
 *   YAGL_BRIDGE_CMD     游戏命令行（含引号的 exe 路径 + 参数；缺省回退为 TARGET）
 *   YAGL_BRIDGE_CWD     游戏工作目录（缺省继承本进程）
 *
 * 退出码：2 = 必填输入缺失；3 = 诱饵创建/属性设置失败；4 = 游戏创建失败；否则透传游戏退出码。
 *
 * 构建：tools/wine-parent-bridge/build.sh（x86_64-w64-mingw32-gcc，-nostdlib 精简；
 * 产物 src/YetAnotherGameLauncher.Core/Resources/yagl-parent-bridge.exe 为嵌入式资源）。
 */
#include <windows.h>

#define YAGL_BRIDGE_MAX 32768

static WCHAR g_decoy[YAGL_BRIDGE_MAX];
static WCHAR g_target[YAGL_BRIDGE_MAX];
static WCHAR g_cmdline[YAGL_BRIDGE_MAX];
static WCHAR g_cwd[YAGL_BRIDGE_MAX];
static char g_attr_storage[512];

static void bridge_fail(const char* message, DWORD code)
{
    DWORD written = 0;
    WriteFile(GetStdHandle(STD_ERROR_HANDLE), message, (DWORD)lstrlenA(message), &written, NULL);
    ExitProcess(code);
}

void entry(void)
{
    SIZE_T attr_size;
    DWORD exit_code = 0;
    static STARTUPINFOW si;
    static STARTUPINFOEXW siex;
    static PROCESS_INFORMATION pi_decoy;
    static PROCESS_INFORMATION pi_game;

    if (GetEnvironmentVariableW(L"YAGL_BRIDGE_DECOY", g_decoy, YAGL_BRIDGE_MAX) == 0 ||
        GetEnvironmentVariableW(L"YAGL_BRIDGE_TARGET", g_target, YAGL_BRIDGE_MAX) == 0)
    {
        bridge_fail("yagl-parent-bridge: missing YAGL_BRIDGE_DECOY/YAGL_BRIDGE_TARGET\n", 2);
    }
    if (GetEnvironmentVariableW(L"YAGL_BRIDGE_CMD", g_cmdline, YAGL_BRIDGE_MAX) == 0)
    {
        g_cmdline[0] = 0;
    }
    if (GetEnvironmentVariableW(L"YAGL_BRIDGE_CWD", g_cwd, YAGL_BRIDGE_MAX) == 0)
    {
        g_cwd[0] = 0;
    }

    /* 1) 诱饵：挂起创建、永不恢复——诱饵进程只作为签名载体存在 */
    si.cb = sizeof(si);
    if (!CreateProcessW(g_decoy, NULL, NULL, NULL, FALSE, CREATE_SUSPENDED,
                        NULL, NULL, &si, &pi_decoy))
    {
        bridge_fail("yagl-parent-bridge: decoy CreateProcess failed\n", 3);
    }

    /* 2) 游戏：父进程属性 = 诱饵句柄（Wine 与 Windows 均支持该属性） */
    attr_size = 0;
    InitializeProcThreadAttributeList(NULL, 1, 0, &attr_size);
    if (attr_size == 0 || attr_size > sizeof(g_attr_storage))
    {
        TerminateProcess(pi_decoy.hProcess, 0);
        bridge_fail("yagl-parent-bridge: attribute list sizing failed\n", 3);
    }

    if (!InitializeProcThreadAttributeList((LPPROC_THREAD_ATTRIBUTE_LIST)g_attr_storage, 1, 0, &attr_size) ||
        !UpdateProcThreadAttribute((LPPROC_THREAD_ATTRIBUTE_LIST)g_attr_storage, 0,
                                   PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
                                   &pi_decoy.hProcess, sizeof(HANDLE), NULL, NULL))
    {
        TerminateProcess(pi_decoy.hProcess, 0);
        bridge_fail("yagl-parent-bridge: parent attribute setup failed\n", 3);
    }

    siex.StartupInfo.cb = sizeof(siex);
    siex.lpAttributeList = (LPPROC_THREAD_ATTRIBUTE_LIST)g_attr_storage;
    if (!CreateProcessW(g_target, g_cmdline[0] ? g_cmdline : NULL, NULL, NULL, TRUE,
                        EXTENDED_STARTUPINFO_PRESENT, NULL, g_cwd[0] ? g_cwd : NULL,
                        &siex.StartupInfo, &pi_game))
    {
        TerminateProcess(pi_decoy.hProcess, 0);
        bridge_fail("yagl-parent-bridge: game CreateProcess failed\n", 4);
    }

    /* 3) 等游戏退出 → 清理诱饵 → 透传退出码（启动日志里保持与直启一致的退出码语义） */
    WaitForSingleObject(pi_game.hProcess, INFINITE);
    GetExitCodeProcess(pi_game.hProcess, &exit_code);
    TerminateProcess(pi_decoy.hProcess, 0);
    ExitProcess(exit_code);
}
