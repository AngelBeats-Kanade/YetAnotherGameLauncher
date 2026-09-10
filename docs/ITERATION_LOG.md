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

## 达标判定（监督者口径）

1. judge 对全部截图（headless 11 张 + 空态/新状态 + 真机）无 P0/P1 失败项。
2. Hyprland 真机：GPU 加速实证活跃、4K 分数缩放下文字锐利、窗口无 SSD 双标题、
   尺寸/最大化状态跨启动保持。
3. 全部测试绿 + 零警告 + format 干净。
4. 未达标 → 继续第 7 轮及以后，不设上限。
