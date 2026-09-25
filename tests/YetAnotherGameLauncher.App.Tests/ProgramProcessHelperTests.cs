using System.Diagnostics;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// Program 启动期子进程辅助函数（Xft.dpi 同步链路）的超时/故障防御：
/// 读取链路的任何失败形态都不得穿出（调用点契约 = "失败按原样启动"）。
/// </summary>
public class ProgramProcessHelperTests
{
    [Fact]
    public void ReadOutputWithTimeout_FaultedReadTask_ReturnsNullInsteadOfThrowing()
    {
        // .NET 10 实测（本仓库 SDK 10.0.x，typeof 三连验证）：AggregateException 直接继承
        // Exception、不再是 SystemException——读任务 faulted 时 Wait() 抛出的聚合异常不会被
        // 调用点的 catch (SystemException) 接住，会一路穿出 Main 炸掉启动。
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("仅 Linux：用 sleep 构造悬挂读进程（本函数族只在 Linux 启动路径执行）");
        }

        using var process = Process.Start(new ProcessStartInfo("sleep", "30")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        });
        Assert.NotNull(process);
        // 预先关掉 stdout 基流：方法内部的 ReadToEndAsync 得到 faulted 任务（而非正常 EOF）
        process.StandardOutput.BaseStream.Dispose();

        var ex = Record.Exception(() => Program.ReadOutputWithTimeout(process, 200));

        Assert.Null(ex);
        if (!process.HasExited)
        {
            process.Kill();
        }
    }

    [Fact]
    public void ReadOutputWithTimeout_OutputDrainedButProcessHung_KillsProcess()
    {
        // F28（035054b 同族残留）：子进程提前关闭 stdout（读到 EOF，读取"成功"完成）但自身挂死时，
        // 成功路径丢弃 WaitForExit 返回值照常返回——进程留成孤儿（Dispose 不杀）。输出已到手，
        // 限时等待仍未退出即 Kill，与超时路径防御对称
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("仅 Linux：用 sh 关闭 stdout 构造读完成但挂死的进程（本函数族只在 Linux 启动路径执行）");
        }

        using var process = Process.Start(new ProcessStartInfo("sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            ArgumentList = { "-c", "exec 1>&-; sleep 30" },
        });
        Assert.NotNull(process);

        var output = Program.ReadOutputWithTimeout(process, 1000);

        Assert.NotNull(output); // stdout 已 EOF：走的是读取成功路径，而非超时路径
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!process.HasExited && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.True(process.HasExited, "读取完成但进程未退出时必须 Kill，不能留成孤儿");
        if (!process.HasExited)
        {
            process.Kill();
        }
    }

    [Fact]
    public void WriteLineAndWaitForExit_Timeout_KillsHangingProcess()
    {
        // MergeXResource 的超时防御契约：WaitForExit 超时后不得把挂死子进程留成孤儿——
        // .NET 的 Process.Dispose 不杀子进程，using 离开作用域后进程继续存活（X 连接楔死的
        // xrdb 可能 indefinite）。同文件 ReadOutputWithTimeout 超时路径有 Kill，防御必须对称。
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("仅 Linux：用 sleep 构造关闭 stdin 也不退出的悬挂进程（本函数族只在 Linux 启动路径执行）");
        }

        using var process = Process.Start(new ProcessStartInfo("sleep", "30")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
        });
        Assert.NotNull(process);

        Program.WriteLineAndWaitForExit(process, "Xft.dpi: 96", 100);

        // Kill 是信号投递，进程真正退出有微小窗口——有界轮询确认终局
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!process.HasExited && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.True(process.HasExited, "WaitForExit 超时后子进程必须已被终止，而不是留成孤儿");
        if (!process.HasExited)
        {
            process.Kill();
        }
    }
}
