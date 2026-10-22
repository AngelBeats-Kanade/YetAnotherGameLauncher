using System.Diagnostics;
using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services.Umu;

/// <summary>
/// Windows 原生 helper 编排测试（2026-10-20）：GUI 子系统 helper 在 Windows 上直接运行，
/// 以挂起的诱饵为父进程拉起子进程，写出 READY 状态，游戏退出后回收诱饵。Linux 上跳过
/// （Wine 侧由 tools/wine-parent-bridge/verify 的 .NET 验证器覆盖）。
/// </summary>
public sealed class WineParentBridgeWindowsTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Helper_CreatesChildWithDecoyParentAndWritesReadyStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows 原生 helper 编排（Linux 由 Wine 验证器覆盖）");
        }

        var helper = _temp.FilePath("yagl-parent-bridge.exe");
        var decoy = _temp.FilePath("Games.exe");
        var result = _temp.FilePath("parent.txt");
        var status = BridgeLaunchStatus.CreateStatusPath(_temp.Path);
        var bytes = DecoyParentBridge.ReadEmbeddedHelper();
        File.WriteAllBytes(helper, bytes);
        File.WriteAllBytes(decoy, bytes);

        var psi = new ProcessStartInfo
        {
            FileName = helper,
            WorkingDirectory = _temp.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment[DecoyParentBridge.DecoyVariable] = decoy;
        psi.Environment[DecoyParentBridge.TargetVariable] = helper;
        psi.Environment[DecoyParentBridge.CommandLineVariable] =
            $"\"{helper}\" --write-parent \"{result}\"";
        psi.Environment[DecoyParentBridge.WorkingDirectoryVariable] = _temp.Path;
        psi.Environment[DecoyParentBridge.StatusVariable] = status;
        // 该测试绕过 SystemProcessRunner 直接 Process.Start；若测试宿主运行在 Rider
        // 调试/热重载会话，必须自己剥离同一组宿主变量，否则 helper 会在 Main 前挂起。
        foreach (var variable in SystemProcessRunner.HostInjectedDotnetVariables)
        {
            psi.Environment.Remove(variable);
        }

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        // 2026-10-20 复审：Windows CI 上把真实 helper 的原子 temp+rename 与 launcher 的
        // 共享读路径放在一起验证（此前两者分别只在 Wine 验证器 / fake runner 覆盖）。
        // 显式两段参数，避免落回旧 API 的单一总预算（Windows CI 慢时启动段会被 READY 段挤占）。
        var statusResult = await BridgeLaunchStatus.WaitAsync(
            status,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken,
            startupTimeout: TimeSpan.FromSeconds(30));
        Assert.Equal(BridgeLaunchState.Ready, statusResult.State);
        Assert.False(File.Exists(status), "READY 状态文件应在读取后清理");

        Assert.True(process.WaitForExit(30_000), "helper 超时未退出");
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.Exists(result), "helper 子进程未写父进程结果");
        Assert.EndsWith("Games.exe", File.ReadAllText(result).Trim(), StringComparison.OrdinalIgnoreCase);
        AssertNoDecoy(decoy);
    }

    /// <summary>helper 退出后诱饵进程应已被 TerminateProcess 回收（按镜像路径比对，忽略同名无关进程）。</summary>
    private static void AssertNoDecoy(string decoy)
    {
        foreach (var process in Process.GetProcessesByName("Games"))
        {
            using (process)
            {
                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // 进程已退出/无权读取模块：不算残留
                }

                if (path is not null)
                {
                    Assert.False(
                        string.Equals(decoy, path, StringComparison.OrdinalIgnoreCase),
                        $"诱饵进程仍存活：{path}");
                }
            }
        }
    }
}
