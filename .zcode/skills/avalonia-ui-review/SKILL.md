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
- **清单（2026-09-25 实测；改动 UI 或新增导出后同步本清单）**：七个画面共 21 张——
  01-15 + 02b + 16 + 17/18 + 10b + 19。归属：`Export_UiScreenshots_ForReview` 11 张；
  `Export_LaunchErrorOverlay_ForReview` 5 张（10/10b/11/12/14，10b=CanRetry=true 覆盖层；
  该导出同时覆盖启动失败覆盖层与 Linux 启动设置卡：umu 启动 + Proton 发行版下拉 + 检查更新按钮）；
  `Export_ProtonUpdateConfirm_ForReview` 1 张（Proton 更新确认覆盖层，内含纱罩压暗的像素级回归断言）；
  `Export_BootSplash_ForReview` 1 张（16-boot-splash 启动遮蔽层）；
  `Export_GachaPage_ForReview` 2 张（17/18 唤取页暗/亮，2026-09-23 补评审 P3-12 覆盖缺口）；
  `Export_EndfieldRealBackdrop_ForReview` 1 张（19 真实海报×终末地详情页——自绘标题移除后顶部无字标冲突的日常形态验收，机器无背景缓存时 Skip，2026-09-25 增）。
  历史：2026-09-20 实测归属更正、2026-09-21 增启动页、2026-09-23 增唤取页、2026-09-25 增真实海报页。
  输出到 `artifacts/ui-review/`（已 gitignore），改动 UI 后重跑并人工/judge 审查。

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

## 3.5 judge 协作与视觉判定纪律（2026-09-25 实锤沉淀；AGENTS.md 只留指针）

- **改机制前先实验，禁止跨框架直觉迁移**：涉及控件模板/主题绘制机制（背景画在哪个区域、样式优先级谁压谁、状态前景落在 presenter 还是本体）时，先构造最小实验（临时色块/探针按钮/放大截图实测坐标）验证机制假设，再写实现。实锤：Fluent `ListBoxItem` 的选中高亮背景 = ContentPresenter 区域、不随 Item 负 Margin 溢出扩展（负 Margin 只撑大 bounds），"外扩选中块让点进块内"方案在 judge 两轮 FAIL 后才被推翻。从 WPF/其他框架带来的绘制直觉在本仓库一律视为未验证假设。
- **judge 是信号不是 oracle**：半透明刷（如 `AppSidebarSelected` 60% α）会让"找纯色边界"类判据把背景本体误读成间隙（实锤：60% α 选中块+点辉光被误判"点在块外"FAIL，放大目视+混合色实测才翻案）；反过来 judge 的 FAIL 也可能是真问题（实锤：水印缺失实为夹具缺陷）。收到 FAIL：先裁剪放大目视 + 像素实测仲裁，确认判据语义后再定改不改。
- **给 judge 交底的执行模板**：采样点用目标元素 `TranslatePoint` 推导的窗口坐标；半透明元素附预期混合色（如 #99304978 画在 #191920 上 ≈ rgb(39,54,85)，α=0x99/255=60%）。
- **视觉判定以像素级/字节级为准，图像分析工具会误报**：实锤 analyze_image 对同一截图连续两次误称"纱罩未压暗"、纯红探针实验误报"无红色"，而解帧字节证明渲染一直正常。颜色/遮罩类结论用帧 `Lock()` 读像素或 PNG 解码采样交叉验证（先例：`Export_ProtonUpdateConfirm_ForReview` 的 LuminanceAt 断言）。
- **Review followup 批次与原始批次同标准**：规则本体在 AGENTS.md「复审与修复纪律」第 8 条（单一事实源，此处只留指针）。

## 4. 已知限制

- headless 渲染无法体现真机动画/透明度混合差异——涉及动效需真机确认。
- 截图输出目录 `artifacts/` 已 gitignore，不要把截图提交进仓库。
