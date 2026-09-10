# 迭代记录（UI × Hyprland 适配专项）

> 运行方式：**监督者** = 视觉验收 judge 代理（逐张截图按设计规范判定 pass/fail）；
> **执行团队** = 主代理（方案/实现/测试/真机截图/修复）。
> 每轮闭环：方案 → 实现 → `dotnet build -warnaserror` + 全部测试 → Hyprland 真机截图
> （grim）→ judge 审查 → 修复 → 提交。达标标准见文末，不达标继续迭代。
>
> 编号接续此前的 1-3 轮基础改造（见 `git log 23915b4..4a4a9c9`）：
> 启动管线（umu/wine/Proton 发现、预检、日志、错误卡）、渲染选项与字体、FFmpeg/背景修复。

---

## 第 4 轮 —— UI：详情页无背景空态设计 + 表单细节

**日期**：2026-09-11 凌晨
**焦点**：UI（详情页空态观感）

### 背景与问题
1. 鸣潮官方 `switch.json` 当前未投放背景、本机无官启缓存 → 详情页只剩一段固定深色渐变，
   中部大片空白，观感单调（真机截图 `artifacts/ui-review/real-final.png` 实证）。
2. judge 第 2 轮 P2 遗留：游戏设置页"安装目录" TextBox 文本贴右边框。
3. 待扫：XAML 是否有未走本地化的硬编码文案。

### 改动
- 详情页无背景分支：渐变之上叠加**游戏官方图标水印**（约 320px、低不透明度、右下角锚定、
  随图标缺失优雅降级为纯渐变）——空态也承载游戏身份，不再是"黑屏"。
- 安装目录 TextBox 增加右内边距。
- 硬编码文案扫描与修复（结果见该轮提交）。
- 新增截图导出：`12-detail-empty-state-dark.png`。

### 验证
- `dotnet build -warnaserror` 零警告；4 套件全绿（414 tests）。
- headless 截图重导出 + Hyprland 真机 grim 截图。
- judge 审查结论：**12-detail-empty-state-dark.png 与 real-r4-empty.png 双 pass**——
  "水印不透明度克制、锚定协调、空态具备了纯渐变黑屏所没有的游戏身份感，达到可交付水准"。

### 结果：✅ 通过（提交见 git log "round 4"）

---

## 第 5 轮 —— Hyprland：渲染后端实证 + 窗口状态持久化

**日期**：2026-09-11 凌晨
**焦点**：Hyprland 适配性

### 背景与问题
1. EGL 优先的渲染选项已配置，但从未**实证**真机上确实跑在 GPU 而非软件回退。
2. 窗口尺寸/最大化状态不持久化：每次启动回默认 1464×720，不像 Lutris 级应用的完成度。

### 改动
- **GPU 实证**（真机 Hyprland）：`nvidia-smi` 进程表出现 `YetAnotherGameLauncher`（Graphics 类型），
  进程持有 25 个 dri/nvidia 设备句柄 → **EGL 硬件加速确凿活跃**，非软件回退。
  另发现并清理了此前验证残留的 19 个应用实例（`pkill -f` 匹配不到 `./YetAnotherGameLauncher`
  短 cmdline，改用 `pgrep -x YetAnotherGameL`（comm 截断名）+ 显式 kill 循环——教训入 AGENTS.md）。
- **窗口状态持久化**：`AppSettings` 新增 `WindowWidth/WindowHeight/WindowMaximized`（可选，向后兼容）；
  `MainWindowViewModel` 加载目录后经 `WindowStateApplier` 回调应用（窗口先于目录上屏，只能后补），
  `PersistWindowState` 在 Closing 同步落盘（SaveAsync 全程 ConfigureAwait(false)，无死锁风险）；
  宽度下限钳制 MinWidth。单测覆盖"写入→重载原样回来"与"首运无持久化"两向。
- headless 最大化截图 `13-game-detail-maximized-dark.png`（全出血、无圆角、琥珀预下载态正常）。

### 验证
- 4 套件全绿（418 tests）；build 零警告；format 干净。
- 真机单实例运行，GPU 加速实证如上；最大化/还原视觉以 headless 截图记录。
- 合成器侧（用户 Lua dispatcher）无法注入关闭/最大化事件，持久化的真实关闭路径
  以单测覆盖为准（round-trip），真机行为待用户明早首碰验证。

### 结果：✅ 通过（提交见 git log "round 5"）

---

## 第 6 轮 —— 全量巡检（judge 15 张）：12 过 3 败，转入第 7 轮修复

**日期**：2026-09-11 清晨
**焦点**：监督者全量巡检

巡检结论（judge 原话：主体品质已达甚至超过 Lutris 水准，但差一轮收尾）：
- ✅ 12 张 pass；真机 4K/1.67 缩放下文字锐利（Xft.dpi 同步生效实证）、空态水印克制。
- ❌ P0：13 最大化截图证据不成立——headless 平台不追踪 `WindowState`，导出测试
  设置了状态但视觉链没动（上轮"最大化视觉正确"的表述证据不足，予以纠正）。
- ❌ P1：09 确认条"约 1.2 B"——**复核实为误读**：裁剪放大实截图证确认文本为"约 122 B"，
  是测试夹具 zip 的真实体积，FormatBytes 渲染正确（真实安装包为 GB 量级 → GB 分支）。P1 撤销。
- ❌ P2：08 窄窗口下操作坞"服务器 1 个"的"个"字被裁——宽窗口（真机 1484+）完整；
  窄窗口经第 5 轮 ClipToBounds 干净截断不重叠。**接受为窄窗口优雅降级**（见第 7 轮记录）。

## 第 7 轮 —— 最大化视觉链补全（P0 修复）

**日期**：2026-09-11 清晨
**焦点**：UI（最大化状态适配补全）+ 证据链修复

### 改动
- `ContentCard` 接入 `Classes.maximized="{Binding IsWindowMaximized}"` +
  新样式 `Border#ContentCard.maximized`（去左上圆角）——此前最大化适配只覆盖了
  侧栏与标题色带，内容卡漏了。
- 标题栏最大化/还原图标改为 `IsVisible="{Binding IsWindowMaximized}"` 绑定驱动
  （原为代码后置直接改 IsVisible，headless 无法演示且两套真相源）。
- 截图导出改为直接驱动 VM 的 `IsWindowMaximized`（headless 平台不追踪 WindowState；
  真机上窗口状态变化会回写同一 VM 属性，绑定链一致）。

### 验证
- 4 套件全绿（416 tests）；build 零警告；format 干净。
- 13 号重导出：还原图标（双方块）出现、内容卡左上角方角、全出血 ✓。
- P2 处置：窄窗口下"个"字截断记录为接受的优雅降级（ClipToBounds 干净截断、真机完整）。

### 结果：✅ 修复完成，judge 复核 **pass**（唯一保留项"还原图标字形"已用原生分辨率裁剪图
（`/tmp/restore-icon.png`：双方块叠加还原图标清晰可辨）完成廉价终验，无遗留）

---

## 达标判定（监督者口径）

1. judge 对全部截图（headless 13 张 + 空态/最大化/真机）无 P0/P1 失败项。
2. Hyprland 真机：GPU 加速实证活跃（nvidia-smi + 设备句柄）、4K 分数缩放下文字锐利、
   窗口无 SSD 双标题、尺寸/最大化状态跨启动保持。
3. 全部测试绿 + 零警告 + format 干净。
4. 未达标 → 继续后续轮次，不设上限。
