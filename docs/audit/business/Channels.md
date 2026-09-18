# Channels 层逐行审计（Kuro 7 文件 + Hypergryph 5 文件，2026-09-19）

通读方式：全部源文件通读 + 逐个未覆盖行上下文核对（上文 sed 摘录）。

## Kuro

| 文件 | 判定 | 未覆盖行与论证 |
|---|---|---|
| KuroChannelApi.cs (175) | ✅ | **170-172**：响应 JSON 损坏 → UpdateException 包装。Phase 4：畸形 indexFile.json 用例（1 例） |
| KuroGachaService.cs (364) | ✅ | 32 行全部为容错分支：日志被占用跳过（84-91）、prefix 枚举守卫（117-138）、缺关键参数 null（166-167）、翻页终止边界、缓存合并容错（187-350 散点）。Phase 4：占用日志（FileShare.None 占位）与畸形 URL 两组用例可覆盖约 12 行；其余为枚举失败容错（构造困难，变异裁决） |
| KuroSwitchConfigClient.cs (211) | ✅ | **126-134**（瞬态失败重试一次后放弃——已有 PersistentFailure 测试覆盖外层，此处为语言循环内层）、**178-179**（背景 URL 推导 null 守卫）。Phase 4：hash 为空的两跳用例 |
| HpatchzApplier.cs (68) | ✅ | **64-66**：裸命令 `.exe` 补试（AGENTS.md 实锤项本体）——**Windows CI 可达**，Linux 上 FindOnPath 主路径已测。Phase 4：Windows 腿 |
| KuroBackdropResolver.cs (29) | ✅ | 100% 覆盖 |
| KuroServiceCollectionExtensions.cs (17) | ✅ | 100% |
| Models/KuroModels.cs (127) | ✅ | internal DTO，经 API 层 JSON 桩全字段反序列化验证（KuroChannelApiTests 的 fixture 即逐字段契约） |

## Hypergryph

| 文件 | 判定 | 未覆盖行与论证 |
|---|---|---|
| GryphlineChannelApi.cs (170) | ✅ | **67**（增量恒 null——包式渠道语义，接口默认实现测试同型已覆盖）、**79-80**（无 patch→null，Predownload_NoPatch 已测——此二行为行号伪影/重复计数）、**131-133**（响应损坏包装）、**137-138**（缺 pkg 抛错）、**163-164**（畸形 URL 文件名 null）。Phase 4：损坏响应 + 缺 pkg 两个用例 |
| EndfieldBackdropResolver.cs (86) | ✅ | **71-72**（rsp 字段缺失→null 的末两个条件）。Phase 4：缺 url 字段用例 |
| GryphlineProtocol.cs (28) | ✅ | 100%（协议常量经 RequestBody 测试锁定） |
| HypergryphServiceCollectionExtensions.cs (30) | ✅ | 100% |
| Models/GryphlineModels.cs (81) | ✅ | 同 KuroModels：JSON 桩即契约 |

## 批次结论

无实锤 bug。未覆盖共 58 行，全部为异常包装/容错/Windows 专属分支；
Phase 4 可补约 20 行（7 个小用例），Windows CI 1 处，其余按容错等价类论证。
