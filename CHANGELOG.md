# Changelog

所有对外可感知的变更记录于此。格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.1.2] — 2026-09-21

v0.1.1 之后的功能版本：背景视频体验全面改造（启动遮蔽预载、切页/切游戏保活不重启、
智能循环 v2、静态→视频提速），附侧栏指示点迁移编舞修复。760 个测试全绿，
行覆盖 85.09%（2026-09-21 实测）。

### 新增

**背景视频体验**

- 启动页遮蔽预载：启动时先在遮蔽页下获取背景再进入主界面——循环点分析与播放并行
  （首帧秒级上屏），门控优先等视频首帧（静态海报仅作 1.5s 宽限后的兜底，5s 超时硬上限），
  进入即见背景动画，无"静态图→视频弹入"的跳变。
- 切页保活：切去设置/关于/抽卡记录等页面再回来，背景视频暂停保活、即时续播——
  不重新加载、无静态图过渡（页外解码泊车不占资源）。
- 切游戏保活：播放器按游戏独占，鸣潮 ↔ 终末地互切双方视频都保活，切回瞬间从暂停帧
  续播；保活上限 2 路暂停 + 1 路在播，超出淘汰最旧（重进自动重启自愈）。
- 智能循环 v2：RGB 彩色评分（不再漏同亮度不同色相的跳变）+ 分块最大差惩罚（局部动作
  跳变不被全局平均淹没）+ 运动趋势匹配；多循环点轮换（每过一圈换切点，重复周期×3）；
  接缝差自适应淡化时长（0.15~0.7s，替代固定 0.6s 长溶解）；修复切口与分析最优帧对
  错位一帧的边界缺陷；无命中时降级取全局最优，不再出现未分析的整段循环接缝。

### 修复

**界面**

- 侧栏指示点迁移编舞（切游戏时的两段式动效）系列修复：起播视频首帧不再挤占编舞窗口
  （时序上移出动画期）、驱动改手写自泵循环（空闲渲染循环降频下 Animation API 掉帧的
  终局方案）、消除"指示点先在目的地闪现再跳回"的基值闪现、落地帧按实时几何收敛。

**可靠性（内部）**

- 背景视频起播的过期任务不再把新会话误标为失活（曾导致续播路径误走重启）；
  播放器会话标志与全停契约收口。

---

### English · 0.1.2 — 2026-09-21

A feature release after 0.1.1: a full overhaul of the backdrop-video
experience (boot splash with preload, keep-alive across page and game
switches, smart loop v2, faster static-to-video transition), plus
sidebar indicator transfer choreography fixes. 760 tests green, line
coverage 85.09% (measured 2026-09-21).

**Added — backdrop video experience**

- Boot splash with backdrop preload: startup fetches the backdrop
  behind a splash cover — loop-point analysis runs in parallel with
  playback (first frame within a fraction of a second) and the gate
  prefers the video's first frame (the static poster is only a
  fallback after a 1.5s grace window; hard 5s cap), so the app opens
  with the animation already playing instead of a poster that later
  pops into video.
- Page-switch keep-alive: switching to Settings/About/gacha records
  and back pauses the backdrop video and resumes it instantly — no
  reload, no poster interlude (the decode loop parks off-page and
  costs nothing while hidden).
- Game-switch keep-alive: one player per game; switching between
  Wuthering Waves and Arknights: Endfield keeps both videos alive,
  and returning shows the parked frame immediately. Keep-alive is
  capped at 2 parked + 1 playing; the oldest parked session is
  evicted beyond that and self-heals by restarting on re-entry.
- Smart loop v2: color-aware (RGB) scoring that no longer misses
  same-luma hue jumps, a max-block penalty so localized subject
  movement isn't buried by the global average, and motion-trend
  matching; multi-point rotation (the cut point rotates each lap,
  tripling the repetition period); seam-difference-adaptive crossfade
  duration (0.15–0.7s replacing the fixed 0.6s dissolve); fixed an
  off-by-one-frame boundary that cut one frame away from the analyzed
  optimum pair; the no-match fallback now takes the globally best
  pair instead of leaving the full-clip seam unanalyzed.

**Fixed — UI**

- Sidebar indicator transfer choreography (the two-phase animation
  when switching games), a series of fixes: video first-frame work no
  longer starves the animation window, the drive loop is hand-pumped
  (the final answer to Animation API stuttering under the
  idle-throttled render loop), the "indicator flickers at the
  destination then jumps back" base-value flash is gone, and landing
  converges on live geometry.

**Fixed — reliability (internal)**

- A superseded video-start task no longer marks the newer session
  inactive (which made the resume path wrongly restart); player
  session-flag and full-stop contracts tightened up.

## [0.1.1] — 2026-09-21

0.1.0 之后的维护版本：三轮全盘代码审计（逐文件线审 + 测试有效性审计 + 独立交叉复核）的
修复批次。无新功能，聚焦可靠性、数据安全与体验回归；731 个测试全绿，行覆盖 85.42%
（2026-09-21 实测）。

### 修复

**网络与代理**

- 修复代理设置完全不生效：组合根漏接代理管理器，保存的直连/自定义代理从未到达共享 HTTP handler。
- 修复运行约 2 分钟后所有联网功能（版本检测/下载/背景/唤取/组件下载）集体失效：终末地命名
  HTTP 客户端的 handler 轮换到期时连带释放了全局共享连接池。
- 网络超时（连接/响应头超时）此前会被当作"用户取消"静默吞掉，现在按瞬态网络错误重试；
  补丁应用、组件下载等后台操作的真正超时会给出明确错误而非无声无息。

**更新与下载**

- 修复清单缺校验字段时健康文件被永久判为损坏、反复重下数 GB 的死循环（增量校验与包式
  完整性检查两处同样中招）。
- 修复渠道返回空版本号时被登记为本地版本、此后永远报"无更新"且无法自愈（现在直接拒绝）。
- 修复鸣潮预下载窗口期的增量清单解析：差分条目实际存放在预下载块中，此前预下载必报
  "增量清单不可用"；预下载提示现在也尊重服务器的预下载开关。
- 包式预下载不再每次清空重下：复用完整暂存包、清单原子写——写入中途崩溃不再作废已下载的数十 GB。
- 修复 Windows 上只读游戏文件死锁增量更新链路；补丁工作目录残留的旧版本拷贝（可能数 GB）
  在更新成功后清理；包式清单同名条目不再互相覆盖（此前第一个包的内容会静默丢失）。
- 修复后台子进程的超时旁路与日志丢失：孙进程持有输出管道会让超时判定被绕过、错误以裸取消
  异常逸出（现在按超时分类报错并终止进程树）；更新/下载/启动日志因依赖注入漏接被静默丢弃。

**配置与数据安全**

- 修复 `games.json` 的 `settings`/`games` 为显式 `null` 时绕过校验、应用以空壳静默启动
  （现在按校验错误提示）；配置保存前同样先过校验（读严写也严）。
- 修复保存假成功：写盘失败时代理/限速设置仍弹"已保存"（重启即回滚）、启动设置的失败草稿
  会留给下一次无关保存静默持久化——现在失败落页内错误槽并回滚内存目录。
- 修复配置迁移（schemaVersion 4/5）写回失败导致整个初始化夭折成空白窗口（现在与 v3 同样
  非致命，下次启动幂等重试）。
- tar 解包的沙箱路径校验收紧（`../targetx/x` 形态此前可落进兄弟目录）；状态文件读取遇
  IO 异常按未知状态处理而非让异常逸出。

**界面与体验**

- 修复游戏设置页「保存启动设置」按钮被改版误删、安装目录回车保存自页面提取以来从未生效——
  草稿字段（启动方式/命令模板/工作目录/环境变量）的编辑此前在"返回游戏"时静默丢失。
- 修复设置页代理地址框的回车保存（页面注释声称支持但从未接线，此前只有按钮能保存）。
- 修复语言切换的一串回归：启动方式下拉选项不随语言刷新甚至显示空白、未保存的环境变量草稿
  被覆盖、唤取页记录列表变空（页面重建后未加载本地缓存）。
- 修复快速切换服务器/语言时上一操作的陈旧结果覆盖新状态（状态行、版本 chip、背景图）：
  引入代际门，迟到的旧结果一律作废。
- 修复详情页图标/背景丢失后不自动恢复（上一轮修复引入的预加载误杀回归；资产加载改用独立代际）。
- 修复背景视频切游戏的一批时序缺陷：旧启动调用的失败清理可能杀掉取代它的新播放；自然播完
  后悬挂的取消源显式摘除。
- 修复 Windows 自启动开关静默失败（注册表命令退出码未检查，开关来回翻转无任何报错）；
  选择不存在的本地 Proton 目录时给出警告而非无声无动作。
- 启动失败覆盖层的归类补全：视频解码启动失败回退海报帧、Steam Runtime 下载超时正确归类、
  页面 async void 的异常路径堵住（此前可崩进程）；同一秒内两次启动的日志不再互相覆盖。

**其他**

- 修复游戏版本号含 7 位数字段时的排序错乱（补位宽度不足）；背景缓存键不再把 `game-a`/`game.a`
  混为同一目录；鸣潮接口的临时文件失败后不再残留 `%TEMP%`；背景图元数据改为原子写。
- 死代码与孤立资源清理（无调用方的 Lutris wine 探测、从未产出的启动方式枚举值、孤立文案键
  与未用的包引用）。

### 文档

- README（中/英）新增面向最终用户的"下载运行"与界面导览章节（附真实界面截图）。

## [0.1.0] — 2026-09-18

首个公开版本。

### 新增

- 双游戏渠道：鸣潮（库洛，增量下载 + HDiffPatch 差分更新）与明日方舟：终末地（鹰角，包式安装）。
- 安装同步：断点续传、MD5 全量校验、损坏自动补下载、差分更新的组级备份回滚；校验修复时
  永不触碰 `Saved/` 存档、Wine prefix（`compatdata/`、应用数据目录）等用户数据。
- Linux 原生 umu 启动链：自动准备 Proton 与 Steam Runtime，DW / GE / UMU-Proton 发行版可选、
  即选即存；「检查更新」检测到上游新版提供页内确认更新（确认卡明示旧版本号与先退出运行的提示，
  更新后自动清理同发行版旧目录）；启动失败按原因分类并给页内修复指引。
- 详情页：官方插画与视频背景（FFmpeg 解码、无缝循环）、版本检测与预下载提示、启动设置卡片。
- 包式渠道登记版本：检测到已按清单安装完整的游戏可零下载登记，直接纳入后续更新编排。
- 唤取记录查询（鸣潮）。
- 设置：明暗主题、简体中文 / English、开机自启（Linux XDG / Windows 注册表）、代理、下载限速；
  服务器切换与启动设置的实际变更落盘后轻提示列出变更字段。
- 窗口：自绘标题栏、明暗主题与双语全量本地化、跨平台窗口状态持久化。

### 平台

- Linux：原生 Wayland 后端优先（实验性）+ X11/XWayland 回退（`YAGL_FORCE_XWAYLAND=1` 逃生舱）。
- Windows 10+。

### 修复（发布前最终批次）

- 修复 toast 轻提示关闭按钮点击无效（Avalonia 命中测试对 `IsHitTestVisible=false` 子树的整树剪枝）。
- 移除弹层（toast / 错误卡 / 修复确认条）的投影：真机 HDR 渲染路径会把模糊阴影呈成边缘生硬的
  灰色矩形板（即"矩形背景层、四角不是圆角"的观感）；层次改由描边与实心底表达。
- 修复 `games.json` 校验失败时可能被空配置覆盖的数据丢失缺陷（现在任何失败分支绝不写盘）。
- 修复清理游离文件可能穿过目录符号链接/junction 删除安装目录之外文件的问题。
- 配置读取失败不再静默呈现空应用，显式提示且保持文件原样；补全局异常日志兜底。
- 安全加固（发布评审）：zip 压缩包的目录条目同样过路径穿越校验（此前仅文件条目受防，恶意包可在
  安装目录外建目录）；tar 解包的符号/硬链接目标与 GitHub 发布资产名加入白名单校验（防上游被篡改时
  经链接或 `../` 资产名写穿到目标目录之外）。
- 修复断点续传死路：`.temp` 已达完整尺寸（上次下载完、落盘前退出，或目标曾被占用后重试）时发出的
  Range 请求被规范服务器以 416 拒绝，曾被误分类为网络错误重试耗尽；现在跳过请求直接校验落盘，
  远端内容变化时丢弃 `.temp` 从零重下。
- 修复差分更新组回滚遗漏"落位中途失败"的文件：此前组内会留下缺失文件且原内容孤悬备份、重试被迫
  整包重下；现在任何一步失败都会完整还原本组（回滚本身失败也会写明残留明细）。
- 修复 Proton / Steam Runtime 首启下载遇网络瞬断（重试耗尽）后丢失重试按钮与本机 Proton 下拉的
  修复 UI（异常未归类成 `LaunchException` 逸出为通用未知错误）。
- 修复 Windows 提权回退（清单要求管理员的 exe）启动日志：此前泄漏文件句柄且日志只有头部，
  现在写明"提权模式无法捕获输出"。
- 背景视频加固：切游戏瞬间旧代帧不再可能"复活"到新详情页（呈现收尾在锁内复查代际）；
  解码中途分辨率变化的片源不再可能原生堆越界（缓冲按帧尺寸重分配）。
- Proton 更新确认卡的版本号不再在连字符处被拆行。

[0.1.1]: https://github.com/AngelBeats-Kanade/YetAnotherGameLauncher/releases/tag/v0.1.1
[0.1.0]: https://github.com/AngelBeats-Kanade/YetAnotherGameLauncher/releases/tag/v0.1.0
