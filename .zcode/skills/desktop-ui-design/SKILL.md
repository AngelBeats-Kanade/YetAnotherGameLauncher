---
name: desktop-ui-design
description: Use when designing or polishing any desktop app UI (layout, spacing, typography, color, dark theme, buttons, states) — 桌面应用设计规范与审美清单。Load alongside avalonia-ui-review whenever UI aesthetics matter; apply the checklist before delivering.
---

# 桌面 UI 设计规范

目标审美：干净、克制、现代（Fluent 风格基准）。核心判断法：**先看层级，再看节奏，最后看细节**。
交付前必须通读 §7 检查清单，并用 `avalonia-ui-review` 渲染截图逐条核对。

## 1. 间距节奏（4pt 网格）

- 只用刻度：4 / 8 / 12 / 16 / 20 / 24 / 32，禁止随手 13、18。
- 关系优先：同组元素 8；组与组 16–24；区块之间 24–32；页面外边距 24–28。
- 卡片内边距 16–20，卡片间距 12–16，不要贴边。
- 间距不对称要有理由：标题与正文之间 4–8，正文与下一区块 16+。

## 2. 字体层级（明确的三层）

| 层级 | 字号/字重 | 用途 |
|---|---|---|
| Display | 22–28 SemiBold | 页面/游戏标题，每屏最多一个 |
| Body | 13–15 Regular | 正文、列表项、按钮 |
| Caption | 11–12 | 辅助说明、版本号、时间（用 `AppTextSecondary` 弱化） |

- 层级靠**字号+字重+颜色**三者共同表达，缺一层就加，不要靠加粗硬撑。
- 中文不挤行：行高 ≥1.4，`TextWrapping` 只用于说明性长文本；单行一律 `TextTrimming`。
- 全大写/超粗不要用于中文。

## 3. 颜色令牌（成对、少量）

- 语义令牌而非裸色：`AppBackdropBaseBrush`（亮/暗渐变背景）+ `AppBackdropGlowBrush`（光晕），
  由 `Controls/AppBackdrop` 渲染，设置页自定义背景图叠加其上；`AppSidebarBackground` / `AppCardBackground` /
  `AppTextSecondary` / `AppAccentBrush` / `AppErrorText` / `AppPredownloadBadge`。
- 强调色只有一个（本项目蓝 `#2E7CF6` 亮 / `#4C8DFF` 暗），用于主按钮、选中态、图标底。
- **暗色主题**：背景为深空蓝黑渐变（`AppBackdropBaseBrush`：`#0C0F1D → #131A2E → #0D1019`）；
  侧栏 `#191920`（alpha D9）、卡片 `#202029`（alpha A6）；强调色略提亮降饱和；
  正文走 FluentTheme 默认前景，插画上正文用 `AppOnArtworkBrush`（`#F2F4F8`），辅助 `#A2A2AB`；禁止大段 `#FFF` 文字。
- **亮色主题**：背景为淡雾蓝渐变（`#EFF2F8 / #E2E9F4 / #EBEEF6`）+ 半透明白卡片（`#D6FFFFFF`），
  靠 1px 边框或极浅阴影分层，不要重阴影。
- 对比度：正文 ≥ 4.5:1，辅助文字 ≥ 3:1（暗色下尤其检查灰字）。

## 4. 按钮与操作层级

- 一屏**一个主操作**（accent 样式），其余默认态；危险操作用红色文案而非红底大按钮。
- 尺寸统一走既有按钮类：详情页按钮系（`glass-onart`/`accent-onart`/`predownload-onart`）圆角 10，
  底部操作坞 `action-dock` 圆角 14；不另做内边距/圆角定制。
- 图标+文字按钮保持图标 16–18px；按钮间距 10–12。
- 禁用态必须可辨（FluentTheme 自带 0.4 透明），配合 ToolTip 说明原因。

## 5. 布局骨架

- 侧栏 240–280px；内容区可读宽度 ≤ 800px，超宽居中留白。
- 对齐线：同屏所有卡片、标题、按钮共享一条左缘。
- 空间利用：不要大片空白也不要塞满——空白本身就是分隔符。
- 空态要设计：图标/说明/下一步动作（"还没有游戏？编辑 games.json 添加"）。

## 6. 状态设计

- 忙碌：进度条 + 一句进行中文案（速度/阶段），隐藏不可用操作而非让用户乱点。
- 错误：`AppErrorText` 红字一句话说清"哪里错了、怎么办"；不弹窗打断。
- 成功：轻提示（状态文字/徽标），不打扰。
- 加载中：骨架或文字占位，不留白屏。

## 7. 交付检查清单（全过才算完成）

1. 亮、暗两套主题逐屏看过截图。
2. 间距全部落在 4pt 刻度，卡片内边距一致。
3. 三层文字层级清晰，辅助文字用的是弱化色。
4. 一屏一个主按钮；按钮尺寸/圆角统一。
5. 圆角值统一（本项目卡片与底部操作坞 14、按钮/图标 10）。
6. 对比度抽检：暗色下的辅助文字、亮色下的强调按钮文字。
7. 长文本有截断策略；单行不换行错乱。
8. 空态/忙碌/错误三态可见。
9. 中文回退字体链正确，无英文-only 残留。
10. 与既有页面风格一致（复用卡片/按钮/徽标模式，不发明新样式）。
