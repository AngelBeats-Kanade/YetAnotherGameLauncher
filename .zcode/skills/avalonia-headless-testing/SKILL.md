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
- **Dispatch 三条实测规则（2026-09-19 探针+位图实验实锤；哨兵 `DispatchSentinelTests` 常驻钉住②的传播属性）**：
  ① **`Dispatch(Func<Task>)`（async lambda）全形态禁用**：本仓库 Avalonia 12.1.2 实为"发射后不管"——lambda 在首个 await 处即被放弃、返回任务不等待完成，await 前后抛的异常与断言一律静默吞掉（`SettingsHeadlessTests` 头注释、`StartupAssetPreloadTests` 审计注记实锤；两文件曾整文件假绿）。CI 有 grep `Dispatch(async` 守卫禁用形态。（历史勘误 2026-09-26：本 skill 曾称"12.1.2 的 `Func<Task>` 可等待重载不被此坑、先例 `BackgroundResilienceTests`"——该先例实为纯逻辑缓存测试（FakeImage，不经 Avalonia 解码），引用无效，勿再据此放开此形态。）
  ② **`Dispatch(Action)` 同步 lambda 必须 `await`**：不 await 则 lambda 只是排队、根本没跑，读局部变量恒为初值；await 后 lambda 在会话线程执行完毕才返回，**异常与断言失败正常传播**（哨兵钉住）。该重载不泵异步续体——lambda 内保持**无 await**（真异步挂进去永久卡死），异步服务调用用 `RunJobs` 泵到完成（先例 `BackgroundImageServiceTests.RunToCompletion`、`StartupAssetPreloadTests`），断言与轮询一律放 Dispatch 之外（先例 `SettingsHeadlessTests`）。文件级落盘断言用 `JsonSerializer` 反序列化后断模型值，不要对原始文件文本做 Contains（JSON 转义与子串巧合都会骗过它——`LinuxFirstRunLaunchTests` 实锤）。
  ③ **Bitmap/RenderTargetBitmap 只能在会话线程**（`await Dispatch(...)` 的同步 lambda 内）：测试线程直调抛 `InvalidOperationException: IPlatformRenderInterface`（渲染接口只在会话线程注册），且该异常会被服务的静默回退吞掉（`BackgroundImageServiceTests` 头注释实锤；2026-09-26 探针实验复核：会话已启动前提下测试线程直调 RenderTargetBitmap/WriteableBitmap 实测抛 `InvalidOperationException: Unable to locate 'Avalonia.Platform.IPlatformRenderInterface'.`，同一会话 Dispatch 内同构造无异常）。只有纯逻辑/缓存类服务（不经 Avalonia 解码，如 `BackgroundResilienceTests` 的 FakeImage 缓存策略）才可测试线程直调。
- **headless 不执行动画**：样式动画与代码 `Animation.RunAsync` 都冻结在首帧（`ForceRenderTimerTick`/真实等待均无效）。模式：先写最终基值再播动画；需要测试落位时用 internal 开关跳过动画（先例：`MainWindow.NavIndicatorAnimationEnabled` / `SplashAnimationEnabled`，经 `InternalsVisibleTo` 暴露）。动画观感只能真机验证或截图 + judge（见 `avalonia-ui-review`）。
- **窗口尺寸语义**：headless 窗口只在 `Show()` 前接受 `Width/Height`；设 `WindowState=Maximized` **不会自动铺满**（真合成器会铺满工作区，headless 不会）——测最大化相关视觉须构造时按 `Screens.ScreenFromWindow` 的工作区定尺寸再 `Show`（先例 `SidebarNavHeadlessTests.CustomTitleBar_ButtonsPresent_AndMaximizeIconToggles`）。`MainWindow.axaml` 写死 `Width="1464" Height="720"` 与 `MinWidth="920"`：测窄窗口必须连 `MinWidth` 一起解除（`MinWidth = 0` + 显式 Width），且 920 恰好触发侧栏自动收起（内容区反而变 852px）——两股力叠加后想测的"放不下的窄布局"可能根本不存在，测试对旧代码假绿（实锤：chips 换行测试设 860 被钳回 920，最长行 790px 在收起态内容区放得下）。要断言目标行为实际发生（如"版本 chip 换到下一行"），而非只断言"不越界"（先例 `GameDetailPage_ChipsRow_LongStatus_WrapsInsteadOfClipping`）。
- 切页后模板在下轮布局构建：`window.UpdateLayout()`；落位类排队任务用 `Dispatcher.UIThread.RunJobs()` 冲刷。
- **像素探针的坐标系**：`CaptureRenderedFrame().Lock()` 读像素用**全窗口帧坐标**——与 page 局部坐标差侧栏宽+页边距、与 scrim 等全出血层局部坐标又差 46px 内容卡偏移；侧栏收放（264↔68）还会让同一"局部"换算出两套窗口值（探针测试默认跑展开态）。几何采样一律由目标元素 `TranslatePoint` 推导到窗口坐标，不写裸数字（先例 `DetailPageHeadlessTests` 纱带探针；judge 交底模板见 `avalonia-ui-review` §3.5）。
- `MainWindowViewModel.Games` 集合在 `InitializeAsync` 之后才有值：先初始化再取 `Games[0]`，否则 `IndexOutOfRangeException`。
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

- **平台分支本机只能执行到一边，另一边是死代码，本地全绿不代表分支正确**（v0.1.1 发布曾被 5 个
  从未在 Windows 上绿过、本地 Linux 全绿的测试阻塞）——对侧正确性由 CI 双平台腿兜底，
  写分支时按下面三条纪律把"另一边"显式建模。
- 测试**不得依赖真机 OS**：本仓库 CI 矩阵在 windows-latest + ubuntu-latest 各跑一遍，
  任何"假设 Windows 行为"的测试都会把 Linux job 打红（2026-09 实修 4 处）。
- App 测试经 `VmFactory.Build(platformInfo:, linuxProtonVersions:)` 注入平台；
  **缺省 = Windows 假平台**（TestSupport `FakePlatformInfo`），保证同一套断言在两个 OS 上
  确定性通过。要测 Linux 分支时显式注入 `new FakePlatformInfo(isLinux: true)`；
  **分支需要的前置状态必须在分支内自己驱动，不能假设对侧路径发生过**（实锤：
  `NativeUmuLaunchRoutingTests` Windows 分支曾因用默认平台导致首运迁移门控不可达、启动假成功）。
- 平台分支期望值惯例（照 `InstallPathTests` / `SystemProcessRunnerTests`）：
  `OperatingSystem.IsWindows() ? "C:/Windows/evil.txt" : "/etc/evil.txt"`。
- 平台专属真实集成（如 `WindowsAutostartService` 真跑 `reg`）：按 OS 分支注入各平台真实现
  （Linux 用 `LinuxAutostartService(home: 临时目录)`），**优先**双平台真跑而非跳过。
  真实平台上被前置门控挡住、不可达的场景（POSIX 执行位/符号链接语义、Windows 独占文件锁、
  权限位注入等）用 `Assert.Skip("原因")` 显式跳过——先例 20+ 处（2026-09-22 审计口径），
  对侧平台由 CI 双平台腿兜底；禁止无条件 Skip（那会让两个平台都失去覆盖）。
- **Windows 上读生产进程正在写的日志**：`StreamWriter` 持写锁期间，测试轮询读同一日志必须以
  `FileShare.ReadWrite` 打开（`File.ReadAllTextAsync` 直接 IOException；Linux 允许并发读所以
  本地测不出，先例 `SystemProcessRunnerTests`）。
- CI 失败注解只有用例名，断言消息需登录 Actions 看完整日志（logs API 要管理员权限，
  check-runs 注解公开可读）。
- 依赖真机状态的扫描（Proton 版本、/proc NVIDIA、$HOME）在测试里一律注入固定值，
  否则测试结果随执行机器漂移。
