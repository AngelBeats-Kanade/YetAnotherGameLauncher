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
