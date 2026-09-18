# UiScreenshotTests 审计

- 方法数：3；判定：❌ 3
- 命中机制：D1（async lambda 吞断言）+ G8（真实外网）+ D5（裸 Thread.Sleep）
- 定位说明：本文件是视觉自检工具（导出 PNG 供人工/judge 审查），但其自动化守卫部分
  （帧非空、状态就位、像素断言）同样必须真实生效，否则"导出的就是没就位的画面"而套件照绿。

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| Export_UiScreenshots_ForReview (:26) | ❌ D1+G8+D5 | :44/:47/:95 真实外网 `HttpClient().GetByteArrayAsync(web.hycdn.cn / mzstatic.com)`（离线必挂，反向使工具不可用）；:51 Dispatch(async) 内 :74 `Assert.NotNull(frame)` 等被吞；:65/:71/:235 裸 Thread.Sleep | 外网字节换本地 fixture 字节（G8）；断言外置（帧存局部变量在 Dispatch 外断言/落盘）；Sleep→有界轮询或 ForceRenderTimerTick 直推 | 16 张核心界面截图可导出且每帧非空（人工/judge 视觉审查的数据源） |
| Export_LaunchErrorOverlay_ForReview (:211) | ❌ D1 | :225 Dispatch(async)，:261 `Assert.True(wuwa.HasLaunchError)` 在内被吞——若启动预检链路坏掉，:263 截图会静默变成"无覆盖层"画面 | 断言外置（out 参数/局部变量带回）；Sleep→RunJobs+ForceRenderTimerTick | 启动失败覆盖层与 Linux 启动设置卡的视觉回归数据源 |
| Export_ProtonUpdateConfirm_ForReview (:290) | ❌ D1 | :307 Dispatch(async)；:326-336 LuminanceAt 的 `Assert.NotNull`/`Assert.True(after<before)`（:342，本文件唯一像素级行为断言）全被吞——纱罩回归守卫实际为空 | 同上 | Proton 更新确认覆盖层纱罩压暗的像素级回归守卫 |

修复备注：三者的 Capture/LuminanceAt 必须留在 UI 线程（CaptureRenderedFrame 是 UI 调用），
正确形态 = `Dispatch(Action)` 同步块内"建窗/交互/抓帧→存局部"，断言与落盘在 Dispatch 外。
`ctx.Vm.IsWindowMaximized = true`（:179）是合法设计（headless 不追踪 WindowState，直接驱动
样式消费的同一 VM 绑定链），不算 G6。
