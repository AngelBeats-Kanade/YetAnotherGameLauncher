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
using Avalonia.Skia;
using YetAnotherGameLauncher;
[assembly: AvaloniaTestApplication(typeof(YetAnotherGameLauncher.AppTests.TestAppBuilder))]   // 注意命名空间是 Avalonia.Headless

namespace YetAnotherGameLauncher.AppTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia(); // 真实 Skia 渲染 + 字体服务：支持截图自检
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

- 需要真实像素（截图/视觉审查）时无需另起会话：上面就是全程序集唯一的共享 App builder，
  始终以 `UseHeadlessDrawing = false` + `UseSkia()` 渲染，在共享会话里
  `window.CaptureRenderedFrame()` 即可抓到真实像素（见 `avalonia-ui-review`）。

## 4. 线程与并发规则

- **测试类只要有任一路径触碰 Avalonia 位图/窗口/平台服务/`VmFactory`/`HeadlessSession`，就必须进 `[Collection("sequential")]`**（2026-09-25 泛化实锤）：这类类会触发或竞争 headless 平台首初始化，与并行组并发时别的测试在 Compositor 构造处炸 `InvalidOperationException`。两种实锤形态：①dlopen 竞争（`VideoBackdropPlayerCtsTests` 引爆 `BackgroundImageServiceTests`）；②`VmFactory` 构建 VM 触发平台初始化（`OpenFolderFailureTests` 不进集合时全量必炸、单跑必过——"单跑过全量炸"不是 flake，是并行冲突的特征指纹）。纯逻辑类不受限。
- 所有 UI 对象操作必须在 `Dispatch` 内；`Application.Current` 属于 headless UI 线程。
- **`Dispatch(Action)` 重载不泵异步续体**（async lambda 即 async void）：
  服务内部 `await HttpClient`/`Task.Delay` 之类的真异步调用挂进去会**永久卡死**（await 后的续体
  排进无人泵的队列）。12.1.2 起另有 `Dispatch(Func<Task>, CancellationToken) → Task` 可等待重载
  且不被此坑（`StartupAssetPreloadTests`/`SettingsHeadlessTests` 在用）。非 UI 的异步服务调用（背景图加载等）在测试 ctor 触发
  `HeadlessSession.Instance` 启动会话后**测试线程直调**即可——Bitmap 解码只依赖全局
  Avalonia locator，线程无关（先例：`BackgroundResilienceTests`）。直接在 Dispatch 外调
  `new Bitmap(...)` 会因 locator 未初始化抛 `IPlatformRenderInterface` 缺失。
- 被测代码若会碰 UI（如切主题），实现层要自检 `CheckAccess()`/`Dispatcher.Post`（`ThemeService` 即范例）。
- **视图层命中/交互回归必须走真实指针**：`window.MouseMove(center); window.MouseDown(center, MouseButton.Left); window.MouseUp(center, MouseButton.Left)`（中心点用 `TranslatePoint` 换算到窗口坐标，先例 `SidebarNavHeadlessTests` / `ToastHeadlessTests`）。直接 `command.Execute()` 的 VM 层测试拦不住命中测试断裂——toast 关闭钮被宿主 `IsHitTestVisible=False` 整树剪掉（Avalonia 语义：祖先剪枝连子级一起剪，子级设回 True 翻不回来），VM 测试全绿而按钮实际点不动，即此故。
- **窗口已挂接后 VM 的 InitializeAsync 必须仍在会话线程内驱动**：`Games` 是 ObservableCollection，
  窗口 Show 后再在测试线程直调 InitializeAsync，集合变更跨线程打进已挂接的 ListBox 绑定直接
  `InvalidOperationException`。模式：`Dispatch(() => { var t = vm.InitializeAsync(); while (!t.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); } })`
  （RunJobs 泵，先例 `BackgroundImageServiceTests.RunToCompletion`、
  `MainWindowChromeHeadlessTests.PersistedMaximizedState_AppliedWhenCatalogLoads`）。
- `Progress<T>` 回调异步投递且**不保证顺序**：测试收集必须用 `ConcurrentQueue` + `SpinWait.SpinUntil`
  等待期望值，禁止断言"最后一条"（本项目踩过的 flaky 根因）。
- **xunit.v3 自带 runner 的 `-method`/`-class` 过滤器对部分用例名静默失灵（Total:0 假象）**（2026-09-25
  一次会话踩三次）：`-method "全名"` 返回 Total:0 但用例实际存在且全量可跑。判定"测试是否存在/被发现"
  以全量运行 + `-xml` 输出核对为准；为 Total:0 长时间推演（怀疑陈旧 bin、怀疑类重复）都是浪费。
- **测试桩实现接口必须显式声明**：C# 不会从公开方法推断接口实现——`public void Dispose()` 存在但
  类未列 `IDisposable` 时 `obj is IDisposable` 为 false，被测代码按契约正确跳过，测试误判被测代码坏
  （2026-09-25 `ImageRetireQueueTests` 实锤：排查绕行两轮——怀疑实现、怀疑构建陈旧——最后发现是桩
  的类型问题）。写桩先写接口清单；调试被测代码前先验证测试自身的前提。
- 测试间状态隔离：临时目录放 fixture，禁写真实用户目录（`TempDir`）。
  **VmFactory 已把模板里的 `~/yagl-test-games` 一并重写到临时目录**——漏改会让首运物化的
  配置把安装根目录指向真实家目录（实踩：测试桩 exe 被写进真实 `~/yagl-test-games`）。
- 共享替身集中在 `tests/YetAnotherGameLauncher.TestSupport/`（FakeDownloader/FakePatchApplier/
  FakeProcessRunner/FakeChannel/FakePlatformInfo/FakeAutostartService/StubHttpHandler/TempDir/
  TestZip/ManualTimeProvider），不要在各测试项目里复制。

## 5. 平台相关测试（Windows / Linux 双平台 CI）

- 测试**不得依赖真机 OS**：本仓库 CI 矩阵在 windows-latest + ubuntu-latest 各跑一遍，
  任何"假设 Windows 行为"的测试都会把 Linux job 打红（2026-09 实修 4 处）。
- App 测试经 `VmFactory.Build(platformInfo:, linuxProtonVersions:)` 注入平台；
  **缺省 = Windows 假平台**（TestSupport `FakePlatformInfo`），保证同一套断言在两个 OS 上
  确定性通过。要测 Linux 分支时显式注入 `new FakePlatformInfo(isLinux: true)`。
- 平台分支期望值惯例（照 `InstallPathTests` / `SystemProcessRunnerTests`）：
  `OperatingSystem.IsWindows() ? "C:/Windows/evil.txt" : "/etc/evil.txt"`。
- 平台专属真实集成（如 `WindowsAutostartService` 真跑 `reg`）：按 OS 分支注入各平台真实现
  （Linux 用 `LinuxAutostartService(home: 临时目录)`），**优先**双平台真跑而非跳过。
  真实平台上被前置门控挡住、不可达的场景（POSIX 执行位/符号链接语义、Windows 独占文件锁、
  权限位注入等）用 `Assert.Skip("原因")` 显式跳过——先例 20+ 处（2026-09-22 审计口径），
  对侧平台由 CI 双平台腿兜底；禁止无条件 Skip（那会让两个平台都失去覆盖）。
- 依赖真机状态的扫描（Proton 版本、/proc NVIDIA、$HOME）在测试里一律注入固定值，
  否则测试结果随执行机器漂移。
