# Changelog

所有对外可感知的变更记录于此。格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.1.0] — 2026-09-17

首个公开版本。

### 新增

- 双游戏渠道：鸣潮（库洛，增量下载 + HDiffPatch 差分更新）与明日方舟：终末地（鹰角，包式安装）。
- 安装同步：断点续传、MD5 全量校验、损坏自动补下载、差分更新的组级备份回滚；校验修复时
  永不触碰 `Saved/` 存档、Wine prefix（`compatdata/`、应用数据目录）等用户数据。
- Linux 原生 umu 启动链：自动准备 Proton 与 Steam Runtime，DW / GE / UMU-Proton 发行版可选、
  即选即存；启动失败按原因分类并给页内修复指引。
- 详情页：官方插画与视频背景（FFmpeg 解码、无缝循环）、版本检测与预下载提示、启动设置卡片。
- 唤取记录查询（鸣潮）。
- 设置：明暗主题、简体中文 / English、开机自启（Linux XDG / Windows 注册表）、代理、下载限速。
- 窗口：自绘标题栏、明暗主题与双语全量本地化、跨平台窗口状态持久化。

### 平台

- Linux：原生 Wayland 后端优先（实验性）+ X11/XWayland 回退（`YAGL_FORCE_XWAYLAND=1` 逃生舱）。
- Windows 10+。

### 修复（发布前最终批次）

- 修复 toast 轻提示关闭按钮点击无效（Avalonia 命中测试对 `IsHitTestVisible=false` 子树的整树剪枝）。
- 移除弹层（toast / 错误卡 / 修复确认条）的投影：真机 HDR 渲染路径会把模糊阴影呈成边缘生硬的
  灰色矩形板（即"矩形背景层、四角不是圆角"的观感）；层次改由描边与实心底表达。
- 修复 `games.json` 校验失败时可能被空配置覆盖的数据丢失缺陷（现在任何失败分支绝不写盘）。
- 修复清理游离文件可能穿过目录符号链接/junction 删除安装目录之外文件的问题。
- 配置读取失败不再静默呈现空应用，显式提示且保持文件原样；补全局异常日志兜底。

[0.1.0]: https://github.com/AngelBeats-Kanade/YetAnotherGameLauncher/releases/tag/v0.1.0
