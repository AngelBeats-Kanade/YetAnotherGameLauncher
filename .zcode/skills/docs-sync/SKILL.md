---
name: docs-sync
description: Use after large refactors or features, when documentation may have drifted from code, or periodically — audit every markdown doc against the actual code, fix all drift, and commit. 文档时效性审计（全量 markdown vs 代码逐条核验 → 修复 → 提交）。Run this after big changes; a scheduled automation also runs it weekly.
---

# 文档时效性审计（docs-sync）

**目标：让全部 markdown 与代码保持同步。** 本仓库文档曾大面积过期（测试数、色值、文件清单、时序图、功能表全烂过），本技能把一次性纠偏的方法论固化下来。审计结论必须以代码为准，文档为嫌疑方。

## 工作流

1. **全量清单**：`git ls-files "*.md"` 列出全部文档（AGENTS.md、README.md、docs/*、.zcode/skills/*/SKILL.md 及附属 md、THIRD-PARTY-NOTICES.md）。工作区改动多时用 `find . -name "*.md" -not -path "./.git/*"` 补充未跟踪文档。
2. **逐文档提取事实性陈述**，重点是这些高腐化类型（历史实证最容易过期）：
   - **点值**：测试/截图/文件数量、色值 hex、尺寸/圆角/Padding 常数、默认值
   - **清单**：目录职责的文件列举、功能表、令牌（token）列表
   - **行为描述**：时序图、线程模型、启动/更新/播放流程、类与方法签名
   - **命令**：文档里的 shell 命令是否仍能跑（路径、参数、产物名）
3. **逐条对照代码核验**：`git grep` 类名/常量/配置键，读关键文件确认签名与行为；数出来的事实（如测试数）以实际运行为准，静态统计只作估算。可并行派 Explore 子代理分文档组核验，最后亲自抽验关键结论再定论。
4. **直接修复**：确认过时的当场改（改前读原文段落，用精确替换，保持各文档既有风格与编号）；拿不准的标"待人工确认"留给用户，不要瞎改。
5. **收尾清扫**：跨全仓 markdown grep 本轮修掉的关键词（如旧 token 名、旧数量、已删类名），确认零残留；提交 `docs: ...` 前缀 commit。

## 输出格式

按文档逐个列出：【过时/错误】（原文要点 → 代码现状 → 改法）、【缺失】（代码有而文档未提的重大项）、【确认无误】（一句话）。最后给严重度分级汇总。

## 与 AGENTS.md 的关系

`AGENTS.md` 的「文档同步」节有**耦合地图**（代码区域 → 文档章节）与点值/单一事实源纪律——本技能是它的全量兜底；日常改动请优先按耦合地图随变更同步，不要攒到审计。
