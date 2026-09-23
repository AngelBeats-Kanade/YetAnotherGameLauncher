using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher;

[ExcludeFromCodeCoverage]
sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't
    // initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 后端决策只算一次，Main 与 BuildAvaloniaApp 共用：
        // 原生 Wayland 下合成器直供分数缩放，Xft.dpi 同步是 XWayland 专属补丁，不再执行
        // （也不应在应用不再运行于 X 时改写会话级 X 资源；逃生舱回退路径仍需它）。
        var useNativeWayland = WaylandBackendPolicy.ShouldUseNativeWayland(
            OperatingSystem.IsLinux(),
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
            Environment.GetEnvironmentVariable(WaylandBackendPolicy.ForceXwaylandVariable));

        if (!useNativeWayland)
        {
            TrySyncXftDpiWithCompositor();
        }

        BuildAvaloniaApp(useNativeWayland)
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// XWayland 内拿不到合成器的分数缩放（X 服务器恒报 DPI 96）：4K + 1.67 这类桌面上，
    /// 整个 UI 按物理像素渲染——字号只有物理 13px，又小又糊。Hyprland 的标准解法是把
    /// 桌面缩放写进 X 资源 Xft.dpi（Hyprland wiki 手工步骤），这里替用户自动做：
    /// 仅当 Xft.dpi 未设置且 hyprctl 可用时，按各显示器缩放合并 96×scale。
    /// 已有 Xft.dpi（用户显式配置过）绝不覆盖；xrdb/hyprctl 失败则按原样启动。
    /// 仅在走 X11/XWayland 后端时由 Main 调用；原生 Wayland 路径由合成器直供缩放，无需此补丁。
    /// </summary>
    private static void TrySyncXftDpiWithCompositor()
    {
        if (!OperatingSystem.IsLinux()
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return;
        }

        if (QueryXResources("Xft.dpi") is not null)
        {
            return; // 用户已配置：绝不覆盖
        }

        var scale = QueryHyprlandMonitorScale();
        if (scale is null)
        {
            return;
        }

        MergeXResource($"Xft.dpi: {Math.Round(96 * scale.Value)}");
    }

    /// <summary>读取 X 资源数据库中指定键的当前值；xrdb 不可用返回 null。</summary>
    private static string? QueryXResources(string key)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("xrdb", "-query")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = ReadOutputWithTimeout(process, 1500);
            if (output is null)
            {
                return null;
            }

            return output.Split('\n')
                .FirstOrDefault(line => line.TrimStart().StartsWith(key, StringComparison.Ordinal))
                ?.Trim();
        }
        catch (SystemException)
        {
            return null;
        }
    }

    /// <summary>向 X 资源数据库合并一行（session 级，重启会话后由本函数再次补写）。</summary>
    private static void MergeXResource(string line)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("xrdb", "-merge")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
            });
            if (process is null)
            {
                return;
            }

            process.StandardInput.WriteLine(line);
            process.StandardInput.Close();
            process.WaitForExit(1500);
        }
        catch (SystemException)
        {
            // xrdb 缺失或写入失败：按默认 DPI 运行
        }
    }

    /// <summary>经 hyprctl 读取 Hyprland 主显示器的缩放值；不可用/异常返回 null。</summary>
    private static double? QueryHyprlandMonitorScale()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("hyprctl", "monitors -j")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            if (process is null)
            {
                return null;
            }

            var json = ReadOutputWithTimeout(process, 1500);
            if (json is null)
            {
                return null;
            }

            // 解析逻辑在纯函数 CompositorScaleParser（可离线直测）；坏 JSON 由本 catch 兜底
            return CompositorScaleParser.ParseMaxMonitorScale(json);
        }
        catch (Exception ex) when (ex is JsonException or SystemException)
        {
            return null;
        }
    }

    /// <summary>
    /// 带超时读取子进程 stdout。必须先异步起读再限时等待：同步 ReadToEnd 会一直阻塞到子进程
    /// 关闭 stdout，排在它后面的 WaitForExit(timeout) 永远执行不到——超时保护形同死代码，
    /// 子进程挂起会把启动卡死在 Main。超时即 Kill 并返回 null。
    /// </summary>
    internal static string? ReadOutputWithTimeout(Process process, int timeoutMilliseconds)
    {
        var read = process.StandardOutput.ReadToEndAsync();
        try
        {
            if (!read.Wait(timeoutMilliseconds))
            {
                KillProcessQuietly(process);
                return null;
            }
        }
        catch (AggregateException)
        {
            // 读任务故障（如 stdout 管道断裂）：.NET 10 实测 AggregateException 直接继承
            // Exception 而非 SystemException，不在此接住就会穿出调用点的 catch (SystemException)
            // 炸掉启动——按"子进程不可用"与超时同路径处理
            KillProcessQuietly(process);
            return null;
        }

        process.WaitForExit(timeoutMilliseconds);
        return read.Result;
    }

    /// <summary>超时/故障路径终结子进程；Kill 前已退出的竞态静默放过。</summary>
    private static void KillProcessQuietly(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // 已退出
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => BuildAvaloniaApp(WaylandBackendPolicy.ShouldUseNativeWayland(
            OperatingSystem.IsLinux(),
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
            Environment.GetEnvironmentVariable(WaylandBackendPolicy.ForceXwaylandVariable)));

    /// <summary>按既定后端决策组装 AppBuilder（决策规则见 <see cref="Services.WaylandBackendPolicy"/>）。</summary>
    /// <param name="useNativeWayland">true 启用原生 Wayland 后端；false 走 X11/XWayland 默认路径。</param>
    internal static AppBuilder BuildAvaloniaApp(bool useNativeWayland)
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

        // Linux 窗口后端二选一：
        // - 原生 Wayland（Avalonia 12.1 实验性后端，Avalonia.Wayland 包）：UsePlatformDetect
        //   不会自动选中，UseWayland() 也无自动回退，故由 WaylandBackendPolicy 先决；
        //   WaylandPlatformOptions 全默认（断线重连开、dma-buf 交换链按合成器/驱动能力自动）。
        // - X11/XWayland：渲染模式显式 EGL 优先——Mesa（AMD/Intel）与 NVIDIA 专有驱动在
        //   XWayland 下 EGL 都比 GLX 稳定（GLX 常见糊化/撕裂），GLX 次之，软件渲染保命。
        // 字体默认族两后端通用：把 Linux 实际存在的 CJK 黑体（Noto CJK / 思源黑体）排在前面——
        // 只写 "Microsoft YaHei UI" 时 fontconfig 模糊匹配会落到楷体/宋体，正文全变形。
        if (OperatingSystem.IsLinux())
        {
            if (useNativeWayland)
            {
                builder = builder.UseWayland();
            }
            else
            {
                builder.With(new X11PlatformOptions
                {
                    RenderingMode =
                    [
                        X11RenderingMode.Egl,
                        X11RenderingMode.Glx,
                        X11RenderingMode.Software,
                    ],
                });
            }

            builder.With(new FontManagerOptions
            {
                DefaultFamilyName = string.Join(", ",
                    "Noto Sans CJK SC",
                    "Source Han Sans CN",
                    "Source Han Sans SC",
                    "Microsoft YaHei UI",
                    "WenQuanYi Zen Hei",
                    "Segoe UI",
                    "Inter"),
            });
        }

        return builder;
    }
}
