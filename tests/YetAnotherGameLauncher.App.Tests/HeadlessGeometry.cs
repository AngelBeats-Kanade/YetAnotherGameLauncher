using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// Headless 几何稳定化工具：页面切入动画（0.3s 横滑）与滚动布局按真实时钟/渲染帧推进，
/// 提前 TranslatePoint 取到的坐标会在下一次输入渲染中漂移（Windows CI 实测偏 33.19px，
/// 足以让中心点落到 62px 宽按钮外）。真实指针点击前先泵到目标中心稳定。
/// </summary>
public static class HeadlessGeometry
{
    /// <summary>
    /// 泵渲染帧直到控件中心在窗口坐标中连续两轮不变（上限 100 轮 / 约 500ms），返回稳定中心点。
    /// 每轮之间留 5ms 墙钟给真实时钟驱动的动画推进。
    /// </summary>
    public static Point SettleAndGetCenter(Window window, Visual target)
    {
        var lastCenter = new Point(double.NaN, double.NaN);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(10);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var center = target.TranslatePoint(
                new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
            if (Math.Abs(center.X - lastCenter.X) < 0.01
                && Math.Abs(center.Y - lastCenter.Y) < 0.01)
            {
                return center;
            }

            lastCenter = center;
            Thread.Sleep(5);
        }

        Assert.Fail("控件中心在 500ms 内未稳定，无法进行真实指针点击");
        return lastCenter;
    }
}
