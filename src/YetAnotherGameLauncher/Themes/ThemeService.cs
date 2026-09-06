using Avalonia;
using Avalonia.Styling;
using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.Themes;

/// <summary>主题切换服务：System 跟随操作系统，Light/Dark 显式覆盖。</summary>
public sealed class ThemeService
{
    public void Apply(ThemeMode mode)
    {
        if (Application.Current is { } app)
        {
            var variant = mode switch
            {
                ThemeMode.Light => ThemeVariant.Light,
                ThemeMode.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default, // 跟随系统
            };

            // Apply 可能从任意线程调用（如后台初始化），跨线程时投递到 UI 线程
            if (app.CheckAccess())
            {
                app.RequestedThemeVariant = variant;
            }
            else
            {
                app.Dispatcher.Post(() => app.RequestedThemeVariant = variant);
            }
        }
    }
}
