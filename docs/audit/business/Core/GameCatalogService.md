# Core/Services/GameCatalogService.cs 逐行审计（232 行，2026-09-19）

| 行 | 判定 | 论证 | 覆盖 |
|---|---|---|---|
| 8-12 GameCatalogValidationException | ✅ | 汇总错误进消息 + Errors 属性；LoadAsync_InvalidContent 断言 Errors 非空 | 已覆盖 |
| 19-21 无参构造 | ✅ 逻辑 / ⚠️ 未覆盖 | 委托 AppPaths.GetConfigFilePath()（生产入口；测试全走显式路径构造） | **未覆盖**——生产组合根使用；Phase 4：直接实例化断言路径后缀 |
| 23-29 构造/属性 | ✅ | — | 已覆盖 |
| 32-41 LoadAsync | ✅ | 缺文件抛 FileNotFound、合法加载——两条直测 | 已覆盖 |
| 44-52 SaveAsync | ✅ | null Catalog 抛异常、原子写（WriteAtomicAsync 承担占用语义） | 已覆盖 |
| 63-92 CreateDefaultFileAsync | ✅ | 四条分支（已存在/模板有效/模板无效回退/缺父目录）各有直测且经重载全量校验 | 已覆盖 |
| 95-104 DefaultCatalog | ✅ | 生成物经 CreateDefaultFileAsync_MissingFile 全量校验 + InstallRoot 断言 | 已覆盖 |
| 107-122 Parse | ✅ | 空 JSON→ValidationException（L113 null 分支）；JsonException 归一（MalformedJson 测试）；校验失败抛出 | 主体已覆盖；L113 `?? throw`（反序列化得 null）**未单独覆盖**——"null 文档"仅理论可达（JSON "null" 输入）。Phase 4：`Parse("null")` 用例 |
| 125-126 Serialize | ✅ | 往返与 camelCase 契约测试 | 已覆盖 |
| 129-157 Validate | ✅ | InstallRoot 空、id 空/非法/重复（大小写不敏感）、必填字段、umuId 格式（Theory×5）、服务器校验、错误累积 | 主体已覆盖；**未覆盖 141,143,145-148**（ProxyMode.Manual 的 proxyAddress http(s) 校验块——GameCatalogValidationTests 缺该块用例，代理地址合法性仅被 VM 层 SaveProxy 间接依赖）。Phase 4 补测：Manual+合法/非法地址 ×2 |
| 159-221 ValidateGame | ✅ | 局部函数 Require 复用正确；服务器 id 重复/缺名直测；umuId 格式直测 | 已覆盖 |
| 224-231 IsValidId/IsValidUmuId | ✅ | 纯谓词；Theory 案例覆盖合法/缺前缀/空后缀/非法字符 | 已覆盖 |

结论：无实锤 bug；11 行未覆盖 = 2 个生产入口/理论分支 + 1 个整块缺失的校验块（proxy manual），
全部列入 Phase 4 补测清单。
