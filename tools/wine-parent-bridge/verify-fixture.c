/*
 * verify-fixture —— 桥验证夹具（dev 工具，不入库构建）。
 * 把"自己的父进程镜像路径"写入命令行指定的结果文件（不用 stdout：wine 下宽字符
 * CRT 输出经管道会截断，社区参考同样建议子探针写显式结果文件）。
 * 用法：verify-fixture.exe <输出文件路径>
 */
#include <windows.h>
#include <tlhelp32.h>
#include <stdio.h>

int main(int argc, char** argv)
{
    char text[4096];
    int pos = 0;
    DWORD pid = GetCurrentProcessId();
    DWORD ppid = 0;
    PROCESSENTRY32W pe;
    HANDLE snap;

    text[0] = 0;
    if (argc < 2)
    {
        return 2;
    }

    snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    pe.dwSize = sizeof(pe);
    if (Process32FirstW(snap, &pe))
    {
        do
        {
            if (pe.th32ProcessID == pid)
            {
                ppid = pe.th32ParentProcessID;
                break;
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);

    HANDLE parent = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, ppid);
    if (parent)
    {
        WCHAR wpath[1024];
        DWORD size = 1024;
        if (QueryFullProcessImageNameW(parent, 0, wpath, &size))
        {
            pos += snprintf(text + pos, sizeof(text) - pos, "PARENT=");
            int n = WideCharToMultiByte(CP_UTF8, 0, wpath, (int)size,
                                        text + pos, (int)sizeof(text) - pos - 2, NULL, NULL);
            if (n > 0)
            {
                pos += n;
            }
            pos += snprintf(text + pos, sizeof(text) - pos, "\n");
        }
        else
        {
            pos += snprintf(text + pos, sizeof(text) - pos, "QUERY_FAILED=%lu\n", GetLastError());
        }
        CloseHandle(parent);
    }
    else
    {
        pos += snprintf(text + pos, sizeof(text) - pos, "OPEN_FAILED=%lu\n", GetLastError());
    }

    HANDLE out = CreateFileA(argv[1], GENERIC_WRITE, FILE_SHARE_READ, NULL,
                             CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (out != INVALID_HANDLE_VALUE)
    {
        DWORD written = 0;
        WriteFile(out, text, (DWORD)pos, &written, NULL);
        CloseHandle(out);
    }
    return 0;
}
