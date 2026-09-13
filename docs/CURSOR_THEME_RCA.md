# Linux 光标不跟随 Hyprland 主题 — 根因分析（RCA）

本文记录 2026-09-13 对「鼠标移到启动器窗口上显示丑陋的黑白位图光标、不遵循 Hyprland 桌面光标样式」问题的完整调查结论。
**未做任何修复**（按决策保持现状），修复选项见 §6，仅供日后参考。
配套文档：[ARCHITECTURE.md](ARCHITECTURE.md)（分层）、[DEVELOPMENT.md](DEVELOPMENT.md)（目录职责）。

## 1. 症状

- 鼠标悬停在启动器窗口客户区：显示经典 X 核心风格的黑白箭头（粗糙位图），观感极差。
- 同一桌面其他区域（桌面、kitty、Qt/GTK 应用等）：显示 Hyprland 自己渲染的光标，样式正常。
- 即：**只有本应用窗口内的光标异常**，且与合成器/桌面配置无关地恒定复现。

## 2. 环境快照（2026-09-13 实测）

| 项 | 值 |
|---|---|
| 系统 | CachyOS（linux 7.2.4-cachyos） |
| 合成器 | Hyprland（Lua 配置 `~/.config/hypr/hyprland.lua`） |
| 应用后端路径 | 原生 Wayland（`WAYLAND_DISPLAY=wayland-1` → `WaylandBackendPolicy.ShouldUseNativeWayland` 为 true；`Services/WaylandBackendPolicy.cs`） |
| Avalonia | 12.1.2（`Avalonia` / `Avalonia.Desktop` / `Avalonia.Wayland` 统一，CPM 见 `Directory.Packages.props`） |
| 会话环境变量 | `XCURSOR_THEME` **未设置**；`XCURSOR_SIZE=24`；`HYPRCURSOR_SIZE=24`（后两者来自 hyprland.lua 的 `hl.env`，未设任何主题变量） |
| 已装光标主题（含 `cursors/` 目录） | `/usr/share/icons/{Adwaita, breeze_cursors, Breeze_Light}`，共 3 个 |
| `default` 主题别名 | **不存在**：`~/.icons` 为空/不存在；`~/.local/share/icons` 只有 Tela 系（无 `cursors/`）；`/usr/share/icons/default` 不存在 |

## 3. 根因链（自上而下）

```
App（无任何 Cursor 设置）
 └─ Avalonia.Wayland 12.1.2  WaylandCursorManager
     └─ wl_cursor_theme_load(null, 24, shm)      ← 主题名硬编码 null、尺寸硬编码 24
         └─ libwayland-cursor
             ├─ null → 字面 "default"（不读 XCURSOR_THEME）
             ├─ xcursor 扫描：~/.icons、$XDG_DATA_HOME/icons、/usr/share/icons…
             │   找 <dir>/default/cursors/ → 本机全部 miss → 0 个光标
             └─ 回退：load_fallback_theme() ← 编译进库的内嵌位图（cursor-data.h）
                 ★ 即「丑光标」本体（经典 X 核心风格黑白箭头）
```

### 3.1 应用层：零光标配置

`git grep -i cursor -- src/` 唯一命中是 `Channels.Kuro/KuroGachaService.cs` 的分页游标 `recordIdCursor`（业务字段，无关）。
所有 `.axaml` 无 `Cursor=` 属性，代码无 `StandardCursorType` / `IStandardCursorFactory` 引用。
**光标样式完全由 Avalonia 平台后端的默认行为决定**，应用层没有任何干预点参与其中。

### 3.2 Avalonia.Wayland 12.1.2：主题名与尺寸双硬编码

`src/Avalonia.Wayland/Server/Transient/WaylandCursorManager.cs`（GitHub tag 12.1.2 实查）：

```csharp
_theme = UnsafeNativeMethods.wl_cursor_theme_load(null, 24, shm.Handle);
```

- 主题名传 `null`，尺寸硬编码 `24`（本机 `XCURSOR_SIZE` 恰好也是 24，尺寸问题暂无感）。
- `WaylandPlatformOptions` 的全部选项（`WlDisplayName`/`DisplayFd`/`EnableReconnects`/`ForceDrawnDecorations`/`GlProfiles`/`UseDmabufSwapchain`/`UseGLibMainLoop`/`ExternalGLibMainLoopExceptionLogger`）中**没有任何光标相关项**，应用侧无从配置。
- 标准光标解析：`StandardCursorType` → 名字别名表（Arrow→`default`/`left_ptr`、Ibeam→`text`/`xterm`、Hand→`pointer`/`hand2` …），逐名 `wl_cursor_theme_get_cursor`；全部未命中回退 Arrow，Arrow 也未命中则隐藏指针。

### 3.3 libwayland-cursor：不读环境变量 + 内嵌位图回退

freedesktop wayland `cursor/wayland-cursor.c`（main 分支实查，本机行为一致）：

1. `wl_cursor_theme_load(NULL, …)`：`if (!name) name = "default";` —— **整条链没有任何 `getenv("XCURSOR_THEME")`**。
2. `xcursor_load_theme("default", 24, …)`：扫描目录由 `XCURSOR_PATH` 决定（未设置时为编译默认 `~/.icons:/usr/share/icons:/usr/share/pixmaps:~/.cursors:/usr/share/cursors/xorg-x11:/usr/X11R6/lib/X11/icons`，另恒含 `$XDG_DATA_HOME/icons`）；只读的环境变量是 `XCURSOR_PATH`/`XDG_DATA_HOME`/`HOME`，**不含 `XCURSOR_THEME`**。找到主题目录后沿 `index.theme` 的 `Inherits` 递归继承（带环保护）。
3. 本机所有扫描目录下都没有名为 `default` 的主题 → 加载 0 个光标；库内的二次重试 `xcursor_load_theme(NULL, …)` 同样落到字面 `"default"`，仍然 0。
4. 最终回退 `load_fallback_theme()`：`#include "cursor-data.h"` 把**编译进库的硬编码位图**灌进 shm pool —— 这就是悬停窗口时看到的丑光标。

### 3.4 为什么桌面其他地方正常（对照）

| 渲染方 | 机制 | 结果 |
|---|---|---|
| Hyprland 自身 | hyprcursor；未配主题 → 内置回退箭头 | 正常观感 |
| 较新的 Qt / Chromium 等 | cursor-shape 协议（`wp_cursor_shape_device_v1`），由合成器渲染 | 跟随合成器 |
| 不自设光标面的客户端 | 合成器默认光标 | 跟随合成器 |
| **Avalonia Wayland（本应用）** | `wl_cursor_theme_load` 按名加载主题位图 | **唯一踩中 "default" 解析失败路径** |

## 4. X11/XWayland 回退路径的补充根因（非当前路径，记录在案）

`src/Avalonia.X11/X11CursorFactory.cs`（12.1.2 实查）：

- 标准光标**主路径直接 `XCreateFontCursor`（核心字体光标）**：`s_mapping` 把 Arrow→`XC_left_ptr`、Ibeam→`XC_xterm` 等；仅 `dnd-copy`/`dnd-link`/`dnd-move`/`dnd-no-drop` 四个名字走 `XcursorLibraryLoadCursor`。
- 即在 X11 路径下无论主题如何配置，标准光标都是核心字体位图（黑白复古）；libXcursor 的主题来源（X 资源 `Xcursor.theme`）根本不在这条路径上。
- 当前默认走原生 Wayland（`Program.BuildAvaloniaApp`），此路径仅在 `YAGL_FORCE_XWAYLAND=1` 逃生舱时激活。

## 5. 为什么常见方案无效（排雷记录）

| 方案 | 结论 |
|---|---|
| 设置 `XCURSOR_THEME` 环境变量 | **无效**。Avalonia Wayland 不读；libwayland-cursor 也不读（§3.3）。这是与 GTK/Qt 直觉相反的最大陷阱 |
| `XCURSOR_SIZE` 环境变量 | 无效（尺寸在 Avalonia 侧硬编码 24）；本机恰好同值，无感知差异 |
| `xrdb` 写 `Xcursor.theme` | 只影响走 libXcursor 的 X11 应用；且 Avalonia X11 标准光标不走 libXcursor（§4） |
| 应用层重绑 `ICursorFactory` | 不可行。平台工厂与 `ICursorImpl` 均为内部类型，应用层无公开扩展点替换标准光标解析 |
| 位图光标覆盖（`new Cursor(Bitmap, hotspot)` 逐控件设置） | 不可行作为通用方案。`WaylandCursorFactory.CreateCursor` 无 scale 概念，1.67× HiDPI 显示器上尺寸错误；TextBox 的 Ibeam、Hand 等需逐控件覆盖，窗口边缘 resize 光标不受应用控制——打地鼠 |
| `XCURSOR_PATH` 指向应用自带主题目录 | 能改扫描路径但**改不了主题名**（硬编码 `"default"`）；除非随应用分发一个名为 `default` 的二进制光标主题，引入体积与许可证负担，否决 |

## 6. 可行修复选项（未实施，仅供日后参考）

### A. `~/.icons/default` 别名（标准机制，推荐）

创建 `~/.icons/default/index.theme`（三行，xcursor 沿 `Inherits` 链解析，重启应用即生效）：

```ini
[Icon Theme]
Name=Default
Inherits=Breeze_Light
```

`Inherits` 可选已安装三者之一：`Breeze_Light`（浅色，最接近 Hyprland 内置箭头）、`breeze_cursors`（深色）、`Adwaita`。
一次写入全系统受益（其他按 `default` 解析的应用同样修复）。纪律：仅在文件缺失时创建，**绝不覆盖已有配置**。

若走应用内自动化，可仿照 `Program.TrySyncXftDpiWithCompositor` 的启动同步模式（Linux + 缺失才写 + 静默失败），决策逻辑放 `Services/` 做纯函数决策表测试（先例：`WaylandBackendPolicy`、`WindowStateMapper`）。

### B. 可选配套：桌面与 app 完全统一

hyprland.lua 的 env 区追加（与 §6A 同主题）：

```lua
hl.env("XCURSOR_THEME", "Breeze_Light")
hl.env("HYPRCURSOR_THEME", "Breeze_Light")
```

不改则桌面仍是 Hyprland 内置箭头，app 显示所选主题，两者存在细微样式差异。

### C. 长期：上游修复

向 Avalonia 提 issue/PR：Wayland 后端透传 `XCURSOR_THEME`/`XCURSOR_SIZE`（`wl_cursor_theme_load` 支持按名+尺寸加载），或实现 cursor-shape 协议（由合成器渲染，彻底跟随桌面）。12.1.2 两者皆无。相关旧 issue：AvaloniaUI/Avalonia#6635（Weird Cursor on Linux）。

## 7. 源码证据索引

| 层 | 文件 | 关键点 |
|---|---|---|
| Avalonia.Wayland | `src/Avalonia.Wayland/Server/Transient/WaylandCursorManager.cs` | `wl_cursor_theme_load(null, 24, shm)`；别名表；回退链 |
| Avalonia.Wayland | `src/Avalonia.Wayland/WaylandCursorFactory.cs` | `ICursorFactory` → worker 转发，无主题参数 |
| Avalonia.Wayland | `src/Avalonia.Wayland/Server/Persistent/WaylandCursor.cs` | 解析失败返回 null = 隐藏指针 |
| Avalonia.Wayland | `src/Avalonia.Wayland/WaylandPlatformOptions.cs` | 无光标选项 |
| Avalonia.X11 | `src/Avalonia.X11/X11CursorFactory.cs` | `XCreateFontCursor` 主路径；仅 dnd 名走 Xcursor |
| wayland | `cursor/wayland-cursor.c` | `null → "default"`；`load_fallback_theme`（cursor-data.h 内嵌位图） |
| wayland | `cursor/xcursor.c` | `XCURSOR_PATH`/`XDG_DATA_HOME`/`HOME`；`Inherits` 递归；无 `XCURSOR_THEME` 读取 |
| 本仓库 | `src/YetAnotherGameLauncher/Program.cs` | `BuildAvaloniaApp` 后端选择；`X11PlatformOptions` 仅配 `RenderingMode` |
| 本仓库 | `src/YetAnotherGameLauncher/Services/WaylandBackendPolicy.cs` | 原生 Wayland / X11 决策 |
