# App/ViewModels 逐行审计：三大 VM + 其余小 VM（2026-09-19）

审计方法：全量结构通读 + 未覆盖行逐段源码核对（SettingsViewModel 命令组、应用背景、自启、
限速、GameItem 启动/安装错误分支、LaunchSettings 平台/浏览分支精读）。

## MainWindowViewModel.cs（1369 行，内嵌 Settings/About/Gacha 等）—— ✅ 已覆盖段无缺陷 / ❗ 138 行未覆盖为**真实补测缺口**

| 未覆盖段 | 内容 | 判定 | Phase 4 |
|---|---|---|---|
| 38,166-167,250,340-341 | 背景图加载容错/首运标记边角 | 容错分支 | 小用例 |
| 512-531 | 配置 IO 失败（ConfigReadFailed）与校验失败双 catch | ConfigFailureTests 已从 Initialize 侧覆盖校验失败；**IO 失败 catch（526-531）未直测** | 配置路径占位为目录（ConfigFailureTests:70 同款手法） |
| 596-597,599 | SetAppBackgroundAsync 的路径不存在早退 | 行为分支 | VM 用例 |
| 653-746 | **应用自定义背景整块**（Set/Pick/ApplySpeedLimit/GetAutostartState/SetAutostart） | 功能块无直测——设置页背景/限速/自启命令组 | VM 用例组（FakeFilePicker + FakeAutostart 注入即全可测） |
| 913-955 | SettingsViewModel.InitializeAsync（自启状态补齐）/SetAutostart 失败提示 | 功能块 | SettingsAutostartTests 已真实覆盖链路（Phase 1 ⚠️G-环境）——断言补齐后归零 |
| 979-1326 | **设置页命令组**：SaveDownloadLimit（解析/负值失败）、ToggleAutostart、SaveInstallRoot（空草稿失败）、SelectedLanguage setter、BrowseAppBackground/ResetAppBackground、OpenConfigFolder 守卫 | 功能块无直测 | VM 用例组（每命令 1-2 例） |
| 1368 | LanguageOption record 尾行 | — | 随组覆盖 |

## GameItemViewModel.cs（941 行）—— ✅ 已覆盖段无缺陷 / ❗ 100 行未覆盖

| 未覆盖段 | 内容 | 判定 | Phase 4 |
|---|---|---|---|
| 76-81 | 假平台下的 ServerCountText/渠道显示边角 | 展示分支 | 小用例 |
| 231,261-281 | 启动预检的 Proton 组件缺失/运行中游戏分支 | 错误分支 | 直调 LaunchAsync 矩阵补 2 例 |
| 366-374,414-417 | 刷新并发去重（_refreshTask 复用）/资产版本装载边界 | 并发守卫 | 有界并发用例 |
| 463-466,491-503 | 预下载按钮态/暂存清理边角 | 分支 | 随预下载矩阵补 |
| 586-617,656-660,691-717 | 启动链路错误分类（wine 缺失已测；**ProtonDownloadFailed/组件准备失败分支未测**）、服务器切换刷新 | 错误分支 | LaunchError 矩阵扩展 |
| 748-775,787-799,823-860 | 安装/更新进度卡生命周期（进度报告消费、完成清卡） | 功能分支 | 进度矩阵 |
| 871-892,932-934 | 抽卡入口/状态行计算边角 | 展示分支 | 随组 |

## LaunchSettingsViewModel.cs（799 行）—— ✅ 已覆盖段无缺陷 / ❗ 79 行未覆盖

| 未覆盖段 | 内容 | 判定 | Phase 4 |
|---|---|---|---|
| 126,212-229 | 启动方式切换（umu↔直连）的草稿改写分支 | 功能分支 | 切换矩阵用例 |
| 238-260 | Proton 发行版检测的绝对路径分支细节 | 已有 Theory 覆盖主路径；嵌套变体 | 小用例 |
| 322-404 | **组件状态文本组装**（NativeUmuStatusText 的 ready/missing/mixed 组合）与检查更新进度 | 展示分支 | 状态矩阵 |
| 453-514 | 检查更新失败/取消分支、确认更新失败分支 | 错误分支 | FakeProvisioner 注失败模式 |
| 559-633 | 保存的环境变量解析边界（空值/重复键/无=行）——部分有测 | 校验分支 | 补边界例 |
| 682-685,764-765 | 浏览可执行文件取消/失败分支 | 已有取消用例；失败分支 | 小用例 |

## 其余小 VM / 桥（全部通读）

| 文件 | 判定 | 说明 |
|---|---|---|
| GachaViewModel.cs (198) | ✅ / ⚠️ 13 行未覆盖 | 拉取失败/空页/忙碌重入分支。Phase 4：3 小用例 |
| LaunchErrorViewModel.cs (128) | ✅ / ⚠️ 9 行 | 打开日志目录/本地选择器空值分支。Phase 4：2 小用例 |
| GameSettingsViewModel.cs (28) | ✅ | 壳转发，1 行未覆盖（构造变体） |
| ToastItem.cs (86) | ✅ / ⚠️ 4 行（自动销毁定时器——Phase 2 记录在案） | Phase 4：定时器用例 |
| SaveMessageSlot.cs (43) | ✅ 100% | SetSuccess/SetFailure/Clear 消息位语义经各保存命令测试 |
| ViewModelBase.cs (7) | ✅ | ObservableObject 空基类 |

## 批次结论

已覆盖段（约 75%）未发现实锤缺陷；138+100+79=317 行未覆盖中**大部分是设置页命令组与
错误分支的功能性缺口**（非容错性不可达），Phase 4 用例规划已内联标注，预计可回收 260+ 行。
