# wine-parent-bridge

`yagl-parent-bridge.exe` —— YAGL 在终末地启动时的**诱饵父进程桥**：游戏（1.6.3+）会校验
"直接父进程"的镜像文件名、Authenticode 签名与签名者证书指纹白名单（实现于游戏目录
`OneChannelLC.dll`，机制与真机实测见 `docs/ARCHITECTURE.md` §3.1 与 `artifacts/bugs.md`）。
本 helper 在 Windows 侧完成挂起诱饵与父进程编排，使游戏通过校验且**不依赖官方启动器**。
Wine/Proton 链与 Windows 原生直启共用同一个 helper。

## 结构

| 文件 | 职责 |
|---|---|
| `WineParentBridge.csproj` | C# helper 工程：`WinExe`（GUI 子系统，Wine 下不开 conhost 黑窗）+ win-x64 自包含单文件 |
| `BridgeContract.cs` | 两侧共享的输入契约（环境变量名、退出码）；Core 侧同名字面量由测试读取本文件锚定 |
| `NativeMethods.cs` | Win32 P/Invoke（CreateProcessW / 属性列表 / Toolhelp 父进程查询） |
| `Program.cs` | 挂起诱饵 → 以诱饵句柄为父进程创建游戏 → 写 `READY` → 等待/回收/透传退出码；`--write-parent` 自检模式 |
| `verify/` | .NET 自检器（取代旧 `verify.sh` + `verify-fixture.c`）；额外断言 `READY` / `ERROR` 状态 |

## 契约

- **输入**（环境变量，均为 Windows 路径）：`YAGL_BRIDGE_DECOY`（诱饵 Games.exe，必填）、
  `YAGL_BRIDGE_TARGET`（游戏 exe，必填）、`YAGL_BRIDGE_CMD`（游戏命令行，缺省回退 TARGET）、
  `YAGL_BRIDGE_CWD`（游戏工作目录，缺省继承）。
- **输出状态**（`YAGL_BRIDGE_STATUS` 指定的文件，原子 temp+rename 写入）：`STARTING` →
  游戏进程创建成功后 `READY`；创建前失败写 `ERROR:<退出码>:<消息>`。**YAGL launcher 启动路径必须
  提供该文件并用 `READY` 判成功**；helper 单独运行/自检可省略。写入失败时 helper 仍继续启动游戏，
  但 launcher 会按超时报错（不静默成功；等待分两段：helper 启动 10 秒 + READY 10 秒）。
  **消费者必须在 READY 之前才把失败视为桥故障**——
  `READY` 之后 helper 透传的是游戏自身退出码（例如启动校验拒绝的 5）。
- **退出码**：`2` = 必填输入缺失；`3` = 诱饵创建/父进程属性失败；`4` = 游戏创建失败；
  否则透传游戏退出码。常量以 `BridgeContract.cs` 为单一来源。

## 构建与自检

- **构建**：无需手工命令。`YetAnotherGameLauncher.Core.csproj` 的 `PublishWineParentBridge` 目标
  在 `AssignTargetPaths` 前把 helper 发布到 Core 的中间目录并以 `LogicalName=yagl-parent-bridge.exe`
  嵌入为资源；产物**不入库**（`src/YetAnotherGameLauncher.Core/Resources/` 下的旧提交已删除）。
  单独构建：`dotnet publish tools/wine-parent-bridge/WineParentBridge.csproj -c Debug -r win-x64 --self-contained true`。
- **自检**（Linux + Wine）：
  ```bash
  dotnet run --project tools/wine-parent-bridge/verify
  # 可选：--helper <path> --wine <path> --prefix <path> --work <path> --keep-work
  ```
  自检器会挑选/发布 helper，在 Wine 下以挂起诱饵为父进程运行 `--write-parent`，断言
  游戏侧看到的父进程 = 诱饵路径、helper 退出后诱饵已被清理，并检查 helper 写出的
  `READY` 与缺输入时的 `ERROR:2:` 状态文件。环境变量 `WINE` / `WINEPREFIX` 可覆盖默认探测。
- **Windows**：Core 的 `WineParentBridgeWindowsTests` 在 Windows 上原生执行同一编排（Linux 上 Skip）。
- 更新 helper 后无需同步哈希锚点（自包含单文件发布不保证跨机逐字节一致）；`DecoyParentBridgeTests`
  会核对 PE 结构与 GUI 子系统（`IMAGE_SUBSYSTEM_WINDOWS_GUI`），console 子系统回归即红。

## 坑

- **Wine 的 `UpdateProcThreadAttribute` 保存指针而非拷贝值**：`lpValue` 指向的句柄变量必须活到
  `CreateProcessW` 返回；提前释放会让后续创建以 `ERROR_INVALID_HANDLE (6)` 失败
  （2026-10-20 复现，见 `docs/PITFALLS.md`）。
- GUI 子系统是硬要求：console 子系统的 helper 在无终端的 Wine 环境下会弹出 conhost 全屏黑窗
  （2026-10-20 真机复现的原始缺陷）。
- 桥工作目录在**应用数据目录**（`<DataDirectory>/bridge/<gameId>`），游戏安装目录只作为诱饵来源
  被读取；旧版 `<installDir>/.yagl/bridge` 由 `DecoyParentBridge` best-effort 清理。
- 状态文件在 READY/ERROR/超时/取消后删除；异常遗留的 `status-*`（含半写 `.tmp`）由下次
  `TryPrepare` 按 1 天阈值清理（正常活跃会话的状态文件最多存在启动窗口 10+10 秒，不会误删并发启动）。
