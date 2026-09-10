# frontend-design（本地移植版）

- **来源**：[anthropics/skills](https://github.com/anthropics/skills) 仓库 `skills/frontend-design/`（与 anthropics/claude-code 仓库 `plugins/frontend-design/skills/frontend-design/` 内副本逐字节一致，已 diff 验证）。
- **许可证**：Apache License 2.0（见 `LICENSE.txt`）。
- **移植日期**：2026-09-06，快照自 main 分支。
- **改动**：`SKILL.md` 正文未做任何修改；本 README 追加 Avalonia 适配注记。

## Avalonia 适配注记（本项目专用）

SKILL.md 以 Web（HTML/CSS）为默认语境。在 YetAnotherGameLauncher（Avalonia 12 桌面应用）中应用时按以下映射执行：

| SKILL.md 中的概念 | 本项目落地方式 |
|---|---|
| 设计 token 系统（color/type/layout/principles） | 全部落到 `App.axaml` 的 `ThemeDictionaries`（亮/暗两套：`AppAccentBrush`、`AppBackdropBaseBrush` 及其光晕刷子 `AppBackdropGlowBrush`、`AppSidebar*`、`AppCard*`、`AppTextSecondary` 等）。**禁止**在控件 AXAML 里写裸 hex；新颜色一律先加 token |
| CSS 选择器优先级互踩 | Avalonia 样式优先级：`Window.Styles` > 主题默认；条件样式用 `Classes.xxx` 动态切换（如状态点的 `installed`/`has-update`），不要靠选择器顺序硬压 |
| Hero 首屏 | 对应游戏详情页：游戏图标在首屏，状态胶囊居页中上方，主操作按钮收拢在底部 `action-dock`。把"最具主题性的一幕"放在首屏：背景插画、游戏图标、版本徽标；主按钮层级交给底部操作坞 |
| 动效（一次编排的时刻） | 用 Avalonia `Transitions` / `Animation` keyframe，150–200ms，只做一处（如页面切换或进度条出现）；不做每卡片 hover 上浮 |
| 排版（一至两个字族、明确字阶） | `FontFamily` 全局默认 + 标题加粗一档即可；字号层级固定几档（12/14/16/20/28），不要随机值 |
| 撰写（按钮动词化、错误给出路） | 中文文案：按钮写"启动游戏/检查更新/预下载"，错误消息说清"哪里错、怎么办"，不道歉不含糊 |
| 截图自检（a picture is worth 1000 tokens） | 用本仓库 headless Skia 截图闭环：跑 `UiScreenshotTests.Export_UiScreenshots_ForReview` 输出 `artifacts/ui-review/*.png`，然后逐张看图——详见 `.zcode/skills/avalonia-ui-review/SKILL.md` |

**避雷清单里的"SaaS 卡片套件"对本项目尤其相关**：卡片是启动器 UI 的基本单元，务必让卡片层级有差别（侧栏条目 / 状态卡 / 信息卡不是同一种卡片），同一圆角和阴影不重复出现在所有层级上。
