using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace YetAnotherGameLauncher.Controls;

/// <summary>
/// 游戏设置页：服务器/位置/启动/依赖/游戏信息（DataContext = <see cref="YetAnotherGameLauncher.ViewModels.GameSettingsViewModel"/>）。
/// 依赖区不定进度条（原生 RichEdit 安装/还原等）由代码后置 <see cref="DrivePulseAsync"/> 自泵驱动——
/// 本页切入时背景视频已暂停，窗口无外部渲染泵，Avalonia 内置 Animation 时钟随空闲渲染循环降频至 ~10Hz
/// （见 docs/ARCHITECTURE.md §3.7）；Task.Delay(16ms) 直写 <see cref="TranslateTransform.X"/> 基值即可维持 ~60fps 平滑帧率。
/// </summary>
public partial class GameSettingsPage : UserControl
{
    /// <summary>脉动往返周期（秒）：三角波左右往返，与启动遮蔽层同节奏。</summary>
    internal const double PulsePeriodSeconds = 1.8;

    /// <summary>脉动滑块默认宽度（px，与 AXAML DependencyPulseBar.Width 对齐）。</summary>
    internal const double DefaultPulseBarWidth = 160;

    /// <summary>脉动驱动节拍：~16ms 一拍（~60fps）。</summary>
    private static readonly TimeSpan PulseTickInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>滑块位移变换（Transform 不能写 x:Name（AVLN2000），运行时挂载）。</summary>
    private readonly TranslateTransform _pulseOffset = new();

    /// <summary>在途脉动泵的取消源（隐藏/卸载视觉树时取消，防孤儿循环）。</summary>
    private CancellationTokenSource? _pulseCts;

    public GameSettingsPage()
    {
        InitializeComponent();
        DependencyPulseBar.RenderTransform = _pulseOffset;
        DependencyIndeterminateBar.PropertyChanged += OnIndeterminateBarPropertyChanged;
        AttachedToVisualTree += (_, _) => SyncPulsePump();
        DetachedFromVisualTree += (_, _) => StopPulsePump();
    }

    private void OnIndeterminateBarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            SyncPulsePump();
        }
    }

    private void SyncPulsePump()
    {
        if (DependencyIndeterminateBar.IsVisible && this.IsAttachedToVisualTree())
        {
            StartPulsePump();
        }
        else
        {
            StopPulsePump();
        }
    }

    private void StartPulsePump()
    {
        if (_pulseCts is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _pulseCts = cts;
        DrivePulseAsync(cts);
    }

    private void StopPulsePump()
    {
        _pulseCts?.Cancel();
        _pulseCts?.Dispose();
        _pulseCts = null;
        _pulseOffset.X = 0;
    }

    private async void DrivePulseAsync(CancellationTokenSource owner)
    {
        var ct = owner.Token;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                var trackWidth = DependencyIndeterminateBar.Bounds.Width;
                var barWidth = DependencyPulseBar.Bounds.Width > 0
                    ? DependencyPulseBar.Bounds.Width
                    : DefaultPulseBarWidth;
                _pulseOffset.X = EvalIndeterminatePulse(
                    stopwatch.Elapsed.TotalSeconds, trackWidth, barWidth);
                await Task.Delay(PulseTickInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 隐藏/卸载取消，属预期
        }
        catch (Exception)
        {
            // async void 防御：装饰性动画异常不崩进程
        }
    }

    /// <summary>
    /// 不定进度脉动纯函数：按经过秒数与轨道/滑块宽度计算当前滑块 X 偏移（三角波往返 ∈ [0, max(0, trackWidth - barWidth)]）。
    /// </summary>
    internal static double EvalIndeterminatePulse(
        double elapsedSeconds, double trackWidth, double barWidth)
    {
        var travel = Math.Max(0, trackWidth - Math.Max(0, barWidth));
        if (travel <= 0 || elapsedSeconds <= 0)
        {
            return 0;
        }

        var phase = elapsedSeconds % PulsePeriodSeconds / PulsePeriodSeconds;
        var triangle = phase < 0.5 ? phase * 2 : (1 - phase) * 2;
        return Math.Round(triangle * travel, 1);
    }
}
