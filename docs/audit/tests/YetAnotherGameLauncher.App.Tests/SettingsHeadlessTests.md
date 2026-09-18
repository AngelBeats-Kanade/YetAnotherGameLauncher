# SettingsHeadlessTests 审计

- 方法数：4；判定：✅ 3 / ⚠️ 1
- 定位：本文件是 AGENTS.md「Dispatch 外断言」纪律的正确先例（文件头自述纪律）。四个 lambda
  均为 `async () =>` 但**无 await**（全同步交互，Dispatch 返回前已完成）——形态合规但 `async`
  关键词是多余的地雷（未来任何人往 lambda 里加一行断言即被吞）。

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| InstallRootBox_Enter_SavesConfig (:45) | ✅ | 断言全在外（:67-77）；有界条件轮询（:69-72）；文件级走 DeserializeConfig（:40-42，反序列化断模型值——D4 的正面样板） | 顺手去掉 lambda 的 `async` 关键词（无 await 的纯同步块） | 安装根目录回车保存（真实键盘路由 handledEventsToo 链路）+ 落盘模型值 |
| InstallDirBox_Enter_SavesLaunchSettings (:81) | ✅ | 断言/轮询/toast 全在外（:101-119） | 同上 | 游戏安装目录回车保存 + 变更轻提示"安装目录" |
| SaveLaunchOptionsButton_Click_PersistsEnvironmentEdits (:128) | ⚠️ D1 变体 | :142 `Assert.IsType<GameSettingsViewModel>(_ctx.Vm.CurrentPage!)` 是 **lambda 内断言**——失败会被吞；幸有 :159 外层同构断言兜底（当前冗余非漏洞），且该变量在 lambda 内确有使用 | 把 IsType 移出 lambda（lambda 内用 `as` + 外层断言非空），恢复"lambda 内零断言"可 grep 性 | 「保存启动设置」按钮真实指针点击 → 环境变量落盘（KEY/VALUE 对象）+ 变更轻提示 |
| EnvironmentBox_WrapsText_SoLastLineNeverCoveredByHorizontalScrollbar (:185) | ✅ | :189-191 out 变量捕获、:218-221 断言全在外（本文件纪律的最标准形态） | 无 | 环境变量框换行（NoWrap+悬浮横滚条遮挡最后一行的事故回归） |
