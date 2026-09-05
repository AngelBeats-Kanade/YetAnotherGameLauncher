---
name: avalonia-headless-testing
description: Use when writing or debugging Avalonia headless tests (Avalonia.Headless, Avalonia.Headless.XUnit, xunit.v3) in this repo — Avalonia 无头测试。Covers the exact working setup for xunit.v3 + Microsoft.Testing.Platform on .NET 10 and the version-trap pitfalls hit in this project.
---

# Avalonia Headless 测试（xunit.v3 / MTP）

本项目实测可用的组合：**xunit.v3 4.0.0 + Avalonia.Headless.XUnit 12.1.2 + .NET 10 SDK 原生 MTP**。
不要再引入 `Microsoft.NET.Test.Sdk` / `xunit.runner.visualstudio`（.NET 10 SDK 下 MTP 已拒绝 VSTest 目标）。

## 1. 测试项目 csproj 必备

```xml
<PropertyGroup>
  <IsPackable>false</IsPackable>
  <OutputType>Exe</OutputType>            <!-- xunit.v3 要求 -->
  <NoWarn>$(NoWarn);xUnit1051</NoWarn>    <!-- 测试内文件操作无需响应取消 -->
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="xunit.v3" />
  <PackageReference Include="Avalonia.Headless" />          <!-- UI 测试项目 -->
  <PackageReference Include="Avalonia.Headless.XUnit" />
</ItemGroup>
```

- 根目录 `global.json` 已声明 `"test": { "runner": "Microsoft.Testing.Platform" }`；`dotnet test` 直接可用。
- 运行单个测试：`dotnet test --project <csproj> --filter-fqn "<完整类型名>.<方法名>"`。

## 2. 命名空间冲突陷阱（CS0435）

xunit.v3 自动生成的入口点命名空间 = 程序集/文件的命名空间**裁掉 "Tests" 尾词**。
测试项目的命名空间若为 `Xxx.App.Tests` 会生成 `Xxx.App`，与 UI 程序集的 `App` 类全名冲突。
对策：测试命名空间避开 `App` 结尾（本项目用 `YetAnotherGameLauncher.AppTests` / `...UiTests`）。

## 3. 测试 App 引导（当前可用形态）

`TestAppBuilder.cs`：

```csharp
using Avalonia;
using Avalonia.Headless;
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]   // 注意命名空间是 Avalonia.Headless

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
```

- `[AvaloniaFact]` / `[assembly: AvaloniaTestFramework]` 在 v3 组合下发现行为随版本漂移（本项目实测
  出现过 discovery 异常/零测试）。**首选稳妥形态：普通 `[Fact]` + `HeadlessUnitTestSession.Dispatch`**：

```csharp
public static class HeadlessSession
{
    public static HeadlessUnitTestSession Instance { get; } =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessSession).Assembly);
}

[Fact]
public async Task Window_Shows_Items()
{
    await HeadlessSession.Instance.Dispatch(() =>
    {
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Assert.Equal(2, window.FindControl<ListBox>("GamesList").ItemCount);
        window.Close();
    }, CancellationToken.None);
}
```

- 需要真实像素（截图/视觉审查）时另起会话：`HeadlessUnitTestSession.StartNew(typeof(Harness))`，
  Harness 自带 `BuildAvaloniaApp()` 且 `UseHeadlessDrawing = false`（见 `avalonia-ui-review`）。

## 4. 线程与并发规则

- 所有 UI 对象操作必须在 `Dispatch` 内；`Application.Current` 属于 headless UI 线程。
- 被测代码若会碰 UI（如切主题），实现层要自检 `CheckAccess()`/`Dispatcher.Post`（`ThemeService` 即范例）。
- `Progress<T>` 回调异步投递且**不保证顺序**：测试收集必须用 `ConcurrentQueue` + `SpinWait.SpinUntil`
  等待期望值，禁止断言"最后一条"（本项目踩过的 flaky 根因）。
- 测试间状态隔离：临时目录放 fixture，禁写真实用户目录（`TempDir`）。
- 共享替身集中在 `tests/YetAnotherGameLauncher.TestSupport/`（FakeDownloader/FakePatchApplier/
  FakeProcessRunner/FakeChannel/StubHttpHandler/TestZip），不要在各测试项目里复制。
