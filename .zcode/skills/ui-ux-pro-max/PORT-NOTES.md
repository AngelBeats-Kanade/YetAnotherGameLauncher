# 移植说明（PORT-NOTES）

- **来源**：[nextlevelbuilder/ui-ux-pro-max-skill](https://github.com/nextlevelbuilder/ui-ux-pro-max-skill) 仓库 `.claude/skills/ui-ux-pro-max/`（jsDelivr 快照，2026-09-06 抓取；`README.md` 为上游原文件）。
- **许可证**：MIT（见 `LICENSE`）。
- **改动**：仅将 `SKILL.md` 中搜索脚本的调用路径从 `${CLAUDE_PLUGIN_ROOT}/.claude/skills/ui-ux-pro-max/scripts/search.py` 改为项目内路径 `.zcode/skills/ui-ux-pro-max/scripts/search.py`；数据、脚本、references 原样保留。
- **数据库规模**：22 个技术栈（含 **avalonia**）、79 种风格、192 套产品配色与推理规则、74 组字体搭配、119 条 UX 准则、105 个图标建议、25 种图表类型。
- **运行要求**：Python 3.x，无第三方依赖（本机已有 3.14）。

## 本项目用法（Avalonia）

本项目的栈检测结果**固定为 `avalonia`**（数据库原生收录 Avalonia 12 规则，见 `data/stacks/avalonia.csv`）。典型调用：

```bash
# 大改版 / 新页面：先出完整设计系统（可调 --variance/--motion/--density 旋钮）
python .zcode/skills/ui-ux-pro-max/scripts/search.py "game launcher desktop dark" --design-system -p "YetAnotherGameLauncher"

# 针对性补查：某个具体关注点
python .zcode/skills/ui-ux-pro-max/scripts/search.py "progress feedback long operation" --domain ux

# 栈级实现规则（Avalonia 专属：编译绑定、x:DataType、ThemeVariant 等 57 条）
python .zcode/skills/ui-ux-pro-max/scripts/search.py "compiled binding themevariant" --stack avalonia
```

**执行顺序**：`--design-system`（大方向）→ `--domain`（补查关注点）→ `--stack avalonia`（实现规则）→ 落地到 `App.axaml` 的 ThemeDictionaries 令牌与 `Views/*.axaml` → 用 `avalonia-ui-review` 技能的截图闭环验收。

**注意**：
- `gsap`、`react`、`web-interface` 等 domain 与 `swiftui`/`flutter` 等栈对本项目无效，忽略其结果；`responsive/viewport/CLS` 类条目按桌面语义折算（窗口缩放、布局重排，而非移动视口）。
- 若搜索返回 0 结果，按 SKILL.md 的规定：重试一次更窄的查询，仍为空则明确声明"无数据库匹配，回退到通用默认"，不要编造。
- 设计系统如需持久化（`--persist`），输出目录指向项目根 `--output-dir .`；MASTER.md 已存在时不要 `--force` 覆盖。
- 119 条 UX 规则全文在 `references/quick-reference.md`，交付前检查清单在 `references/pro-rules.md`——按 SKILL.md 指示按需读取，不要每次全量加载。
