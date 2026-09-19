# Phase 3 业务审计索引 —— App 层（2026-09-19）

> 2026-09-20 补记：Phase 4c 提取的 `Services/CompositorScaleParser.cs`（hyprctl 缩放解析纯函数）
> 在下表 Services-Views-Excluded.md 的审计时点之后新增，非漏审——判定见 REMAINING.md P4
> "排除文件可回收"行（直测全绿、空数组返回 null 边角已修）。

| 记录文件 | 覆盖的源文件 | 状态 |
|---|---|---|
| UmuComponentProvisioner.md | UmuComponentProvisioner (1157) | 完成 |
| ViewModels.md | MainWindowViewModel (1369)、GameItemViewModel (941)、LaunchSettingsViewModel (799)、GachaViewModel、LaunchErrorViewModel、GameSettingsViewModel、ToastItem、SaveMessageSlot、ViewModelBase | 完成 |
| Services-Views-Excluded.md | BackgroundImageService、LocalizationService、SeamAnalyzer、FrameSurface、WindowStateMapper、WaylandBackendPolicy、ThemeService、LocBridge、PrerollHandoff + 6 个排除文件 + Views/Controls/.axaml | 完成 |

## App 层结论

- 无实锤业务 bug（已覆盖段约 75% 经 Phase 1 测试矩阵 + 本轮逐段核对）。
- **317 行 VM 未覆盖为真实功能缺口**（设置页命令组/启动错误分支），非容错不可达——Phase 4 补测主体。
- 排除文件 ~2600 行全部给出结构性理由；约 50 行纯逻辑可回收。
- Phase 4 优先级：①UmuComponentProvisioner Runtime 正流程（~110 行/单点最大）②设置页命令组 VM 用例
  （~130 行）③GameItem 启动/安装错误矩阵（~60 行）④LaunchSettings 切换与组件状态矩阵（~50 行）。
