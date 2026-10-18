# wine-parent-bridge

`yagl-parent-bridge.exe` —— YAGL 在 Wine/Proton 链上启动终末地时的**诱饵父进程桥**：
游戏（1.6.3+）会校验"直接父进程"的镜像文件名、Authenticode 签名与签名者证书指纹白名单
（实现于游戏目录 `OneChannelLC.dll`，机制与真机实测见 `docs/ARCHITECTURE.md` §3.1 与
`artifacts/bugs.md`）。本 helper 在 wine 侧完成挂起诱饵与父进程编排，使游戏通过校验
且**不依赖官方启动器**。

- **产物**：`src/YetAnotherGameLauncher.Core/Resources/yagl-parent-bridge.exe`（嵌入为程序集资源；
  运行时由 `DecoyParentBridge` 落盘到游戏目录 `.yagl/bridge/`）。
- **构建**：`./build.sh`（需要 `x86_64-w64-mingw32-gcc`；`-nostdlib` 精简 PE，仅 KERNEL32 导入；
  `--no-insert-timestamp` 可复现——同一源码两次构建逐字节相同，嵌入资源哈希因此可核对来源）。
- **自检**：`./verify.sh`（在 wine 下用夹具断言：目标进程看到的父进程 = 诱饵路径；
  桥退出后诱饵进程被清理）。环境变量 `WINE` / `WINEPREFIX` 可覆盖。
- **行为契约**（输入环境变量、退出码）：见 `bridge.c` 头注释。
- 更新 helper 后：`./build.sh` 重建 → `./verify.sh` 自检 → 同步
  `tests/.../DecoyParentBridgeTests.cs` 的 `EmbeddedHelperSha256` 锚点。
