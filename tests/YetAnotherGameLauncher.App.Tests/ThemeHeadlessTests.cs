using Avalonia;
using Avalonia.Styling;
using YetAnotherGameLauncher.AppTests;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Themes;
using Xunit;

namespace YetAnotherGameLauncher.UiTests;

[Collection("sequential")]
public class ThemeHeadlessTests
{
    [Fact]
    public async Task Apply_Dark_SetsDarkActualVariant()
    {
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var service = new ThemeService();

            service.Apply(ThemeMode.Dark);

            Assert.Equal(ThemeVariant.Dark, Application.Current!.ActualThemeVariant);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Apply_Light_SetsLightActualVariant()
    {
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var service = new ThemeService();

            service.Apply(ThemeMode.Light);

            Assert.Equal(ThemeVariant.Light, Application.Current!.ActualThemeVariant);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Apply_System_UsesDefaultRequestedVariant()
    {
        await HeadlessSession.Instance.Dispatch(() =>
        {
            var service = new ThemeService();

            service.Apply(ThemeMode.Dark);
            service.Apply(ThemeMode.System);

            // Default 表示跟随系统，实际值由平台决定
            Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);
        }, CancellationToken.None);
    }
}
