using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(YetAnotherGameLauncher.AppTests.TestAppBuilder))]

namespace YetAnotherGameLauncher.AppTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia(); // 真实 Skia 渲染 + 字体服务：支持截图自检
}

/// <summary>
/// 手动创建/获取本测试程序集的 Headless 会话（在 UI 线程上执行 Avalonia 操作）。
/// 相比 xunit 框架级集成，该方式与 xunit 主版本解耦、行为可预期。
/// 懒初始化用锁保护：并行测试类的首次访问可能并发进入——Avalonia headless 平台
/// 初始化不是线程安全的（2026-09 实测：双初始化在 ServerCompositor 构造处崩溃），
/// 我们侧串行化是唯一能根治的位置；与 [Collection("sequential")] 互为双保险。
/// </summary>
public static class HeadlessSession
{
    private static readonly object Gate = new();
    private static HeadlessUnitTestSession? _instance;

    public static HeadlessUnitTestSession Instance
    {
        get
        {
            if (_instance is { } session)
            {
                return session;
            }

            lock (Gate)
            {
                _instance ??= HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessSession).Assembly);
            }

            return _instance;
        }
    }
}
