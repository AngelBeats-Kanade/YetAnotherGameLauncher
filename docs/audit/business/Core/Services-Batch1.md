# Core/Services 逐行审计：HttpFileDownloader / SpeedLimiter / LocalStateService（2026-09-19）

## HttpFileDownloader.cs（192 行）—— ✅ 逻辑 / ⚠️ 9 行未覆盖

| 行 | 判定 | 论证 | 覆盖 |
|---|---|---|---|
| 27-73 DownloadFileAsync | ✅ | 重试循环分类正确：校验失败删 .temp 从头（Md5Mismatch 重下 3 次实测）、网络错误保 .temp 续传（Transient/Exhausted 测试）、退避线性（RetryBaseDelay=1ms） | 已覆盖 |
| 80-93 ReplaceDestination | ✅ 逻辑 | 落盘替换单独分类（目标占用绝不落入网络重试——AGENTS.md 头号跨平台坑的防线），错误消息给可操作指引 | **未覆盖 86-91**（占用/只读分支）：Linux rename 语义下无法稳定复现。Phase 4：目标位置预置同名**目录**（复用 GameBackdropService 的占位手法）构造 IOException |
| 95-171 DownloadAttemptAsync | ✅ | 416→校验失败语义（416 测试）；200 忽略 Range→重写（IgnoreRange 测试）；206+temp>0 才续传（Range 起点精确断言）；ExpectedSize=0 不走捷径（零字节回归）；.temp 已完整→零请求直接校验（CompleteTemp 测试）；进度单调（Phase 2 已补两两断言）；限速挂钩 | 主体已覆盖；**未覆盖 165-167**（限速等待分支：Limiter.Acquire>0 → Task.Delay。现有下载测试均未配置限速）。Phase 4：注入共享 SpeedLimiter（BytesPerSecond 小值 + ManualTimeProvider 不可行——Limiter 在 HttpFileDownloader 内部默认 System 时钟；需构造子注入 SpeedLimiter 实例并置小带宽，等待真实短延时即可命中） |
| 173-191 Verify | ✅ | 尺寸/MD5 双校验，各自独立异常 | 已覆盖（SizeMismatch/Md5Mismatch） |

## SpeedLimiter.cs（57 行）—— ✅ 逻辑 / ⚠️ 5 行未覆盖

| 行 | 判定 | 论证 | 覆盖 |
|---|---|---|---|
| 15-35 构造/属性 | ✅ 逻辑 | setter 清空排队窗口（SetZero_ResetQueue 测试）；Math.Max(0) 钳负 | **未覆盖 21-24**（getter——被 VM 绑定读取，测试未直接读。Phase 4：一行 getter 断言）；26 未覆盖为 setter 尾行（lock 块收尾行计数伪影） |
| 38-56 Acquire | ✅ | 预算排队/放行/零速直通全测（ManualTimeProvider 虚拟时钟）；`Math.Max(durationTicks,1)` 防零除类边界 | 已覆盖（L45 `_nextFreeTimestamp=0` 零速直通分支经 Acquire_Unlimited 覆盖） |

## LocalStateService.cs（43 行）—— ✅ 逻辑 / ⚠️ 3 行未覆盖

| 行 | 判定 | 论证 | 覆盖 |
|---|---|---|---|
| 18-37 Load | ✅ 逻辑 | 缺文件 null；gameId/serverId 不匹配 null（他游戏 state 不误判——GameUpdateServiceTests 专项）；JsonException→null（损坏视为未安装，契约注释自证） | **未覆盖 33-35**（损坏 JSON→null 分支）。Phase 4：写坏 state.json 后断 Load 返回 null |
| 40-42 SaveAsync | ✅ | 原子写委托（WriteAtomicAsync 已审计） | 已覆盖 |

结论：三文件无实锤 bug；17 行未覆盖均为容错分支或平台不对称路径，Phase 4 补测清单已列 5 项。
