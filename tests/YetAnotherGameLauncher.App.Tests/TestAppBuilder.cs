using Avalonia;
using Avalonia.Headless;
using YetAnotherGameLauncher;

[assembly: AvaloniaTestApplication(typeof(YetAnotherGameLauncher.AppTests.TestAppBuilder))]

namespace YetAnotherGameLauncher.AppTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// 手动创建/获取本测试程序集的 Headless 会话（在 UI 线程上执行 Avalonia 操作）。
/// 相比 xunit 框架级集成，该方式与 xunit 主版本解耦、行为可预期。
/// </summary>
public static class HeadlessSession
{
    public static HeadlessUnitTestSession Instance { get; } =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessSession).Assembly);
}
