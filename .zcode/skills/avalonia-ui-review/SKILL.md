---
name: avalonia-ui-review
description: Use before delivering or after changing any Avalonia UI — render real window screenshots headlessly to PNG, inspect them visually, and iterate until they pass the design checklist. Avalonia UI 视觉自检循环（渲染截图 → 看图 → 改进）。ALWAYS run this loop after UI changes; never ship UI you have not looked at.
---

# Avalonia UI 视觉自检循环

**铁律：改了 UI 却没有渲染截图亲眼看过 = 没完成。** Agent 可以直接查看 PNG 图片（Read 图片文件），
因此"改 UI → 渲染 → 看图 → 再改"是完全可行的闭环，也是把 UI 从"能跑"提升到"好看"的唯一手段。

## 1. 截图工具（本项目已内置）

`tests/YetAnotherGameLauncher.App.Tests/UiScreenshotTests.cs`：
复用 `TestAppBuilder`（`UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia()`，
真实 Skia 渲染 + 文字排版），在共享 headless 会话内用 `window.CaptureRenderedFrame()`
抓帧并保存 PNG 到 `artifacts/ui-review/`。

运行（本机 `dotnet test` 可能发现 0 个测试，直跑编译产物更可靠，--filter-fqn 实测零匹配）：

```bash
dotnet tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll \
  -method "YetAnotherGameLauncher.UiTests.UiScreenshotTests.Export_UiScreenshots_ForReview"
```

要点（改 UiScreenshotTests 前先读）：

- 截图无需独立会话：整个测试程序集只有一个共享会话 `HeadlessSession.Instance.Dispatch`
  （`TestAppBuilder.cs` 的 `GetOrStartForAssembly`），其唯一 builder 始终以
  `UseHeadlessDrawing = false` + `UseSkia()` 构建，默认就能抓到真实 Skia 渲染像素。
- `CaptureRenderedFrame()` 内部会触发渲染计时器 tick 并返回最后一帧（可能返回 null——先 `window.Show()`）。
- 保存：`frame.Save(path, new PngBitmapEncoderOptions())`。
- 数据必须真实：用 `VmFactory` 样例数据（两个游戏、假渠道状态：已安装/有更新/预下载可用都摆出来）。
- 截图窗口固定 1120×720（`UiScreenshotTests` 内写死，与主窗口默认 1464×720 无关，保证构图稳定）。
  张数与各导出方法归属**不在此复制**（枚举即副本必漂移）——以 AGENTS.md「UI / 无头测试已知坑」
  的"视觉自检"条为准（该条带实测日期），另有专项导出内含像素级断言。

## 2. 看图检查清单（逐项过，亮暗各一遍）

1. **层级**：一眼能分清标题/正文/辅助文字三层吗？辅助文字是否用了 `AppTextSecondary`？
2. **间距节奏**：所有间距是否落在 4/8/12/16/24 的刻度上？卡片内边距、区块间距是否一致？
3. **对齐**：图标、标题、按钮左缘是否对齐一条线？进度条/文本与卡片边缘内边距一致吗？
4. **按钮层级**：主操作是否唯一且醒目（accent）？次操作是否弱化？禁用态是否可辨？
5. **对比度**：暗色主题下灰字是否可读？亮色下卡片与背景是否有区分（阴影或色差）？
6. **状态完整**：空态/忙碌（进度条）/错误提示是否可见且不突兀？
7. **文本**：有无被截断（TextTrimming）、换行错乱、中英混排基线不齐？
8. **暗色不纯黑、亮色不刺白**；强调色两主题下饱和度是否协调？
9. **一致性**：圆角、图标风格、各卡片风格是否统一？
10. **CJK**：中文字体回退正确、行高不挤。

## 3. 迭代方式

- 每轮只改一个层面（先布局间距 → 再颜色层级 → 最后细节），改完立即重跑截图对比。
- 改 AXAML 时对照 `avalonia-ui` 的约定（主题刷子成对、伪类样式、Classes 扩展按钮）。
- 全部通过清单后跑 `dotnet test` 再交付，并把关键截图结论写进交付说明。

## 3.5 judge 协作纪律（2026-09-25 实锤；规则本体在 AGENTS.md「UI 坑」对应条，此处只留执行细节）

- **judge 是信号不是 oracle**：收到 FAIL 先裁剪放大目视 + 像素实测仲裁判据语义，再定改不改（正反实锤与半透明刷判据陷阱见 AGENTS.md「UI 坑」judge 仲裁规则条）。
- **给 judge 交底的执行模板**：采样点用目标元素 `TranslatePoint` 推导的窗口坐标；半透明元素附预期混合色（如 #99304978 画在 #191920 上 ≈ rgb(39,54,85)，α=0x99/255=60%）。
- **改观感机制前先最小实验**：本技能内可操作的实验形式 = 临时色块/探针按钮/裁剪放大实测坐标（规则与先例见 AGENTS.md「UI 坑」"改机制前先实验"条）。
- **Review followup 批次与原始批次同标准**：规则本体在 AGENTS.md「复审与修复纪律」第 8 条（单一事实源，此处只留指针）。

## 4. 已知限制

- headless 渲染无法体现真机动画/透明度混合差异——涉及动效需真机确认。
- 截图输出目录 `artifacts/` 已 gitignore，不要把截图提交进仓库。
