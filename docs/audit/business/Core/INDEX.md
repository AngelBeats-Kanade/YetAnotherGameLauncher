# Phase 3 业务审计索引 —— Core（2026-09-19）

覆盖：Core 全部 33 个源文件逐行通读完毕。判定：**无实锤 bug**；缺口为容错分支/平台专属路径/幂等矩阵未测。

| 记录文件 | 覆盖的源文件 | 未覆盖行 |
|---|---|---|
| Models.md | 12 个 Models | 0 |
| Utilities.md | Hashing、FileUtilities | 5 + 29 |
| GameCatalogService.md | GameCatalogService | 9 |
| GameLauncherService.md | GameLauncherService | 21 |
| Services-Batch1.md | HttpFileDownloader、SpeedLimiter、LocalStateService | 9 + 5 + 3 |
| Services-Batch2.md | GameInstallService + 100% 文件批（GameUpdateService/ManifestVerifier/NetworkProxyManager/PackageInstallerService/InstallPath/PlatformInfoFactory/Autostart×3） | 2 + 0 |
| SystemProcessRunner.md | SystemProcessRunner | 63（45 行 Windows 专属提权路径） |
| GameBackdropService.md | GameBackdropService | 21（含 1 处疑似防御性死代码 L183-185 待变异裁决） |
| Services-Batch3.md | IncrementalUpdateService、CompatTools、LinuxPlatformInfo、AppPaths | 16 + 6 + 9 + 2 |
| Umu.md | UmuPrefix、NativeUmuLauncher、VdfMiniParser、ToolManifest、UmuEnvironment、SteamRuntimeCatalog、UmuPaths | 75 + 47 + 22 + 13 + 9 + 2 + 7 |

## Phase 4 补测优先级（Core 部分）

1. **UmuPrefix 幂等矩阵**（75 行，纯文件系统逻辑完全可测）
2. **VdfMiniParser 畸形输入组**（22 行，外部数据攻击面）
3. SystemProcessRunner env+日志组合 / 瞬秒进程 / 启动失败释放（~10 行）
4. GameLauncherService 防御分支直调（模板空/引号裸段/prefix 失败，~15 行）
5. GameBackdropService 异常回退分支 + 死代码裁决（21 行）
6. Windows-CI 可达：IsExecutableFile true 分支、AppPaths Windows 目录、提权回退段（尝试）
7. 其余散点容错行（JsonException→null 类，每处 1-3 行）

## 平台不对称残留（无法在本平台覆盖，逐行论证保留）

- SystemProcessRunner 提权 740 回退（~45 行）——Windows 安装器清单语义
- GameLauncherService L106-107（Windows 生产 PATH 委托）
- UmuPrefix EnsureUserExecute Windows no-op
