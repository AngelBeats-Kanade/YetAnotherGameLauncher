# Core/Services/GameBackdropService.cs 逐行审计（322 行，2026-09-19）

| 行 | 判定 | 论证 | 覆盖 |
|---|---|---|---|
| 23-40 构造 | ✅ | 按游戏信号量串行化（防并发重复下载）；DefaultCacheRoot | **未覆盖 40**（缺省 cacheRoot——测试全部显式注入；生产组合根使用。Phase 4：一行构造断言或经组合根测试） |
| 47-76 ResolveAsync | ✅ | 未知渠道 null；按游戏门；版本门控命中零网络（专项测试）；锁释放 finally | 已覆盖 |
| 79-87 ResolveCachedAsync | ✅ | 绝不触网；区域一致才命中 | 已覆盖（Hit/Miss 双测） |
| 90-94 GetCachedGameVersion | ✅ | 空版本返回 null | 已覆盖（NoCache 测试 + 版本门控链路） |
| 97-103 IsCacheFreshFor | ✅ | 区域 Ordinal 相等 + 文件在盘 + requireVersionMatch 时版本一致 | 已覆盖（门控/区域变化/旧格式升级三测） |
| 106-119 ResolvedFromCache | ✅ 逻辑 | 本体缺失 null；海报缺失回退直链 | **未覆盖 109-110**（缓存元数据在而本体文件被外删——理论可达但现有测试不删缓存文件后直接走此分支。Phase 4：解析成功后删本体文件再 ResolveCached 断 null） |
| 121-171 ResolveCoreAsync | ✅ 逻辑 | 解析异常回退缓存（用户取消不吞——IsCancellationRequested 过滤精确）；地址/类型未变仅升级元数据不重下（版本门控双测试）；变化才重下；缓存兜底；直链兜底 | 主体已覆盖；**未覆盖 131-132,134-135**（resolver 抛异常→回退缓存的 catch——ResolverFails 测试走的是 Resolver 返回 null 而非抛异常。Phase 4：resolver 改为抛 HttpRequestException 的用例） |
| 174-231 TryDownloadAsync | ✅ 逻辑 | 海报失败不拖垮本体（专项测试）；畸形 URL/非 http(s) 异常归类回退（catch 列表注释自证）；stale 临时文件清理；清理失败不遮盖主异常 | **未覆盖 184-185**（本体下载返回 null——DownloadToFileAsync 失败返回 null 的路径：EnsureSuccessStatusCode 抛的是异常而非 null，null 仅在……细读：DownloadToFileAsync 不返回 null（失败走异常），L183 的 null 分支实为**防御性死代码**。Phase 4：变异验证判定（改为不可达后删分支或补直链 404→异常路径的等价确认）；**未覆盖 221-223,225-227**（stale 清理及其容错——下载失败场景的清理循环。Phase 4：404 后断言无 download-* 残留） |
| 234-270 DownloadToFileAsync | ✅ 逻辑 | 扩展名解析（畸形→.img 兜底）；流式写盘；临时名+原子改名；**占用换时间戳备用名**（Undeletable 测试） | 已覆盖 |
| 272-301 ReadMeta/WriteMeta | ✅ 逻辑 | 旧格式/损坏元数据 null（LegacyMeta 测试）；写失败不致命 | **未覆盖 284-286**（meta 损坏 JsonException→null。Phase 4：写坏 meta.json 后断门控未命中走重解析）、**297-298**（写失败容错——只读目录构造） |
| 303 Sanitize | ✅ | gameId 只留字母数字（缓存目录名安全） | 已覆盖（间接：全部缓存测试经此路径） |
| 314-321 BackdropMeta | ✅ | json 显式命名 + 旧格式缺省字段 null 语义 | 已覆盖（LegacyMeta 用例） |

结论：无实锤 bug。21 行未覆盖中 1 处疑似防御性死代码（L183-185，Phase 4 变异验证裁决），
其余为异常回退/容错分支，Phase 4 补测清单已列 6 项。
