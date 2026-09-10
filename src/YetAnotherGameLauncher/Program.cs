using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using System;
using System.Diagnostics;
using System.Linq;

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
        TrySyncXftDpiWithCompositor();
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// XWayland 内拿不到合成器的分数缩放（X 服务器恒报 DPI 96）：4K + 1.67 这类桌面上，
    /// 整个 UI 按物理像素渲染——字号只有物理 13px，又小又糊。Hyprland 的标准解法是把
    /// 桌面缩放写进 X 资源 Xft.dpi（Hyprland wiki 手工步骤），这里替用户自动做：
    /// 仅当 Xft.dpi 未设置且 hyprctl 可用时，按各显示器缩放合并 96×scale。
    /// 已有 Xft.dpi（用户显式配置过）绝不覆盖；xrdb/hyprctl 失败则按原样启动。
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

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1500);
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

            var json = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1500))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // 已退出
                }

                return null;
            }

            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateArray()
                .Select(m => m.TryGetProperty("scale", out var scale)
                            && scale.ValueKind == JsonValueKind.Number
                    ? scale.GetDouble()
                    : double.NaN)
                .Where(s => s is >= 0.5 and <= 5)
                .OrderByDescending(s => s) // 多显示器取最大缩放：UI 宁可大不可小
                .FirstOrDefault() is { } scaleValue && !double.IsNaN(scaleValue)
                ? scaleValue
                : null;
        }
        catch (Exception ex) when (ex is JsonException or SystemException)
        {
            return null;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

        // Linux：Avalonia 12 只有 X11 后端（Wayland 会话下经 XWayland 运行）。
        // 渲染模式显式 EGL 优先——Mesa（AMD/Intel）与 NVIDIA 专有驱动在 XWayland 下
        // EGL 都比 GLX 稳定（GLX 常见糊化/撕裂），GLX 次之，软件渲染保命（零 GPU 也能跑）。
        // 字体默认族把 Linux 实际存在的 CJK 黑体（Noto CJK / 思源黑体）排在前面——
        // 只写 "Microsoft YaHei UI" 时 fontconfig 模糊匹配会落到楷体/宋体，正文全变形。
        if (OperatingSystem.IsLinux())
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
