---
name: avalonia-ui-review
description: Use before delivering or after changing any Avalonia UI — render real window screenshots headlessly to PNG, inspect them visually, and iterate until they pass the design checklist. Avalonia UI 视觉自检循环（渲染截图 → 看图 → 改进）。ALWAYS run this loop after UI changes; never ship UI you have not looked at.
---

# Avalonia UI 视觉自检循环

**铁律：改了 UI 却没有渲染截图亲眼看过 = 没完成。** Agent 可以直接查看 PNG 图片（Read 图片文件），
因此"改 UI → 渲染 → 看图 → 再改"是完全可行的闭环，也是把 UI 从"能跑"提升到"好看"的唯一手段。

## 1. 截图工具（本项目已内置）

`tests/YetAnotherGameLauncher.App.Tests/UiScreenshotHarness.cs`：
以 `UseHeadlessDrawing = false`（真实 Skia 渲染，含文字排版）启动独立 headless 会话，
用 `window.CaptureRenderedFrame()` 抓帧并保存 PNG 到 `artifacts/ui-review/`。

运行：

```bash
dotnet test --project tests/YetAnotherGameLauncher.App.Tests \
  --filter-fqn "YetAnotherGameLauncher.AppTests.UiScreenshotTests.Export_UiScreenshots_ForReview"
```

要点（改 Harness 前先读）：

- 截图会话必须与测试会话隔离：`HeadlessUnitTestSession.StartNew(typeof(UiScreenshotHarness))`，
  该类型自带 `BuildAvaloniaApp()`（`UseHeadlessDrawing = false`）。默认测试用的 headless drawing
  抓不出真实像素。
- `CaptureRenderedFrame()` 内部会触发渲染计时器 tick 并返回最后一帧（可能返回 null——先 `window.Show()`）。
- 保存：`bitmap.Save(path)`（Avalonia `Bitmap.Save(string)`）。
- 数据必须真实：用 `VmFactory` 样例数据（两个游戏、假渠道状态：已安装/有更新/预下载可用都摆出来）。
- 亮暗主题各截一套；多页面（游戏详情/设置）各截一张。窗口尺寸用真实尺寸（1120x720）。

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

## 4. 已知限制

- headless 渲染无法体现真机动画/透明度混合差异——涉及动效需真机确认。
- 截图输出目录 `artifacts/` 已 gitignore，不要把截图提交进仓库。
