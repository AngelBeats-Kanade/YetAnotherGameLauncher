# Phase 0 基线记录

- 实测日期：2026-09-19（.NET 10.0.11 / Avalonia 12.1.2 / xunit.v3 4.0.0 + MTP / dotnet-coverage 18.11.2）
- 构建基点：a333e07 (main)；`dotnet build -warnaserror` → 0 警告 0 错误

## 测试基线（直跑 DLL；3 轮连跑验证稳定性）

| 套件 | 执行数 | 第1轮 | 第2轮 | 第3轮 |
|---|---:|---|---|---|
| Core.Tests | 245 | 全绿 0.83s | 全绿 0.84s | 全绿 0.86s |
| Channels.Kuro.Tests | 49 | 全绿 0.13s | 全绿 0.14s | 全绿 0.14s |
| Channels.Hypergryph.Tests | 17 | 全绿 0.13s | 全绿 0.13s | 全绿 0.14s |
| App.Tests | 255 | 全绿 20.2s | 全绿 20.4s | 全绿 20.4s |
| **合计** | **566** | 0 失败 | 0 失败 | 0 失败 |

执行数 566 > 静态统计 527 个测试方法，差异来自 Theory 数据展开。

结论：套件级稳定（无时序性假失败）。**这不证明测试有效**——假绿（断言被吞/断言空心）
在"代码正确"时与真绿不可区分，需 Phase 1 审计 + Phase 4 变异验证才能暴露。

## 覆盖率基线（仓库历史首次真实测量）

采集命令（可推导复现）：

```bash
dotnet tool install --global dotnet-coverage
for name in Core.Tests Channels.Kuro.Tests Channels.Hypergryph.Tests App.Tests; do
  dotnet-coverage collect -f cobertura -o "$name.cobertura.xml" \
    dotnet "tests/YetAnotherGameLauncher.$name/bin/Debug/net10.0/YetAnotherGameLauncher.$name.dll"
done
node artifacts/audit/tools/coverage-summary.mjs SUMMARY.md *.cobertura.xml
```

- 说明：直跑 DLL 是 xunit.v3 自带 runner，不存在 MTP `--coverage` 开关
  （docs/DEVELOPMENT.md §9 的"dotnet test --collect"路径在本机受 0 发现问题限制）；
  实测采用 `dotnet-coverage` 包裹进程采集，等价出行级 cobertura。
- **总计 4608/5739 可计行 = 80.29%**（仅本仓库 src/ 下 .cs；排除 obj/ 生成物与 .axaml 伪行；
  `[ExcludeFromCodeCoverage]` 类不出现在 XML，天然不计）
- 缺口最大文件（<70%）：UmuPaths 2/9、UmuPrefix 44/119、Hashing 5/10、
  UmuComponentProvisioner 368/628、NativeUmuLauncher 91/138、SystemProcessRunner 127/190、
  FileUtilities 61/90、FrameSurface 32/47、LaunchErrorViewModel 24/33
- 原始 XML 与 91 文件完整表在 `artifacts/coverage/baseline/`（本地生成，不入库）

## Dispatch 探针结论

见同目录 DISPATCH-PROBE.md：`Dispatch(Action)` 内同步断言失败可传播（不吞）；
`Dispatch(Func<Task>)` 全形态（await 前/后）断言失败均被吞——14 个 async lambda 形态测试实锤假绿。
