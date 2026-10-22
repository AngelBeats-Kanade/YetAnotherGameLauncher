# Changelog

所有对外可感知的变更记录于此。格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.1.3] — 未发布

0.1.2 之后的功能版本：界面全面翻新（详情页身份、侧栏提示与短状态、危险操作样式、
对比度门禁、字号与间距栅格）；依赖安装（Linux，内置思源黑体）；自定义启动选项对齐
Steam（%command% 占位符）并新增「功能开关」区；系统托盘驻留 + 关闭按钮行为设置
（退出应用 / 关闭窗口驻留托盘，下载与依赖安装后台继续）；资源包档位、实时下载进度
与速度；hpatchz 自动供给与鸣潮 krpdiff 增量更新链终局修复；终末地更新链路（分卷解压修复、官方废弃清单清理、改走官方 v2 增量补丁）；终末地启动链黑窗修复（桥 helper 迁移 C# GUI 子系统）与 Windows 直启接入。
1572 个测试全绿（2026-10-22 实测）。

### 新增

**界面全面翻新**

- 详情页：识别交给窗口标题（游戏页「<游戏> - 应用」、随语言切换）与侧栏选中态，不再自绘标题（官方背景自带 logo）；未安装且不可启动时显示空态引导卡（渠道/最新版本/服务器数/安装目录 + 指引），启动按钮直接隐藏（安装即主操作）。
- 侧栏：收起后的图标项悬停显示游戏全名；状态行改用短文案（英文检测态不再截断成断词）；收起态指示点收进选中高亮块内。
- 危险操作视觉分级：修复重装确认（红字红边）与 Proton 更新确认（错误卡内危险按钮、浅红悬停水洗）不再与建设性主操作同形；启动失败覆盖层主次按钮随「可否重试」互换；更新确认文案前置破坏性动词（「删除旧版并更新」）。
- 可读性门禁：新增主题令牌对比度门禁（白字-强调色、徽章、版本金字、操作坞、抽卡稀有度、错误文本，按半透明背景合成的最坏情形、亮暗双主题均 ≥4.5:1）；亮暗强调色按门禁加深（悬停再深一档）；抽卡稀有度色收进主题令牌。
- 字号与间距：17 档字号收敛为 9 档（12/13/14/15/16/18/20/24/28）；全仓 axaml 间距对齐 4 步栅格（≥8 为 4 的倍数、<8 为偶数）。
- 交互一致性：设置页安装目录改内联浏览按钮（与游戏设置页位置卡一致）；启动设置保存按钮脏感知（草稿无差异时熄灭、保存/回滚后复位）；启动失败覆盖层期间不叠新 toast；关于页新增项目主页入口（幽灵 GitHub 图标 + 悬停显示完整 URL，打开失败给警告 toast）。

**依赖安装（Linux）**

- 游戏设置页新增「依赖」卡（仅 Linux 显示）：为当前游戏的 Wine prefix 手动安装可选组件，逐项给出状态点与安装/重装按钮、进度行与独立反馈槽。
- 首版内置 CJK 字体（思源黑体，Adobe 发布版 2.004R，MD5+尺寸双重校验）：XDG 缓存下载（断点续传、多游戏复用）→ 防卫式解压 → 装入 `windows/Fonts` → 单次 `wine reg import` 落 28 条字体注册与 51 条替换映射（宋体/雅黑/MS Gothic 等简繁日韩系统字体名 → 思源黑体），修复 Wine 下缺字形的方块乱码。
- 安装前自动解析目标（umu 内置 wine / 系统 wine 两种形态）并做 prefix 健康预检；prefix 未初始化（未启动过游戏）或缺 Proton 时给出可操作提示而非报错；可取消，失败不留完成标记（不留半装状态）。

**启动卡「功能开关」**

- 游戏设置页启动卡新增开关区「功能开关」（仅 Linux 显示）：Wayland 驱动（`PROTON_USE_WAYLAND`）、DLSS 升级（`PROTON_DLSS_UPGRADE` + `PROTON_ENABLE_NVAPI`）、Proton 日志（`PROTON_LOG` + `PROTON_LOG_DIR`）三个开关，随保存按钮落盘；模板直启与原生 umu 两条启动链均注入（特性键优先于用户环境变量）。
- umu 链另设 `STEAM_COMPAT_LIBRARY_PATHS` 指向游戏安装目录。
- 环境变量编辑框只显示用户变量：托管键（`PROTONPATH`、运行器生成项等）不再出现在框内，保存时托管键与用户变量合并、用户值优先；切换 Proton 发行版即时落盘。

**自定义启动选项：改名与 Steam 风格词法**

- 「环境变量」框更名为「自定义启动选项」，标签旁新增「ⓘ」图标，悬停显示填写规则（Material info 字形，圆底反白）。
- 输入格式对齐 Steam 启动选项：空白（含换行）分隔多条 `KEY=VALUE`，值含空格用引号包裹（如 `MAP="coast 11"`）——未加引号的值内空格不再被吞进值、`=` 两侧不再容忍空格；支持双/单引号、反斜杠转义与引号跨行。
- 设置页开关区「启动选项」更名为「功能开关」（避免与启动选项框撞名）。

**自定义启动选项支持 Steam `%command%` 占位符**

- 「自定义启动选项」框内未写 `%command%` 时末尾由启动器自动补全：`KEY=VALUE` 条目照常作为环境变量注入启动进程——与 Steam 启动选项格式一致，无需手写占位符。
- 显式写一行 `%command%` 时，其后的内容作为游戏命令行参数追加在游戏可执行文件之后（如 `%command% -dx11`），模板直启与原生 umu 两条启动链均生效；`games.json` 亦可经 `launch.arguments` 数组直接配置。
- 引号/转义词法不变；`%command%` 最多出现一次，裸 token（想给游戏传参的高频误解）、参数区引号未闭合与重复占位符均有专属错误提示，裸 token 的提示直接指路 `%command%` 写法（中英双语）。

**hpatchz 自动供给（鸣潮增量更新免手动安装）**

- 应用增量更新时若 PATH 上没有 hpatchz，启动器自动下载社区验证构建（鸣潮 krpdiff 与开源 HDiffPatch 全线构建不兼容——真机实证官方 v4.8.0/v5.1.3 与 master 均无法读取，退出码 109；供给 ww-manager 社区打包的 hpatchz.exe，MD5 校验，实测产出与官方清单一致）。Linux 上经 wine 运行（系统 wine 或已装 Proton 自带，独立 WINEPREFIX 不污染环境）；自备二进制（PATH 或显式路径）始终优先且原生直跑。

**资源包档位与按档下载（鸣潮，2026-10-19 扩充）**

- 游戏设置页新增「资源包档位」下拉（HD / SD / UHD / 跟随游戏内设置，仅鸣潮显示）：启动时以官方 `-krqlv=<hd|sd|uhd>` 参数传给游戏，umu 与直启两条启动链均生效；`games.json` 的 `launch.resourceQualityTier` 可直接配置（白名单校验）。
- 档位（2026-10-19 起）同时决定下载来源：全量同步/校验修复按所选档构建清单（裁掉其它档位目录 + 补目标档位包），增量更新只取与档位无关的基础差量、档位包由更新后的全量修复轮补齐；档位包下载需服务器带官方档位索引（`servers[].options.resourcePackIndexUrl`，内置样例为鸣潮官服给出；国际服/B 服实测无档位索引，档位只作启动参数且设置页给出提示）。存量配置由 `schemaVersion 7` 迁移补键。
- 档位"追加式共存"：切换档位不删旧档内容——同步收尾清理与增量废弃清单都不碰保留档位目录（渠道按档位裁剪废弃清单，消费端另有纵深豁免）；设置页「清理其他档位」预览释放量与目标档位、确认后只删各档位清单点名且解析后位于该档位目录下的文件与下载半成品 `.temp`（选中档位未完整落盘时给出补齐前可能无法运行的警示）。
- 已安装且本地档位与所选档位不一致时主按钮变「下载资源包」（更新 + 补包一体），状态点用"可行动"配色而非绿色。

**下载暂停与续传（2026-10-19）**

- 下载/更新/预下载忙碌区新增「暂停」按钮：取消本次操作并保留全部已下载与暂存产物（不写版本、不报错、不清理），任何阶段可停。
- 跨会话续传：操作开始时写 `.yagl/download-session.json`（含操作种类与游戏/服务器归属）、成功收尾才清；重开应用时主按钮显示「继续下载」，并按种类派回被暂停的动作（预下载 → 预下载链；内容已下完/应用中被停 → 应用链续跑，暂存清单保留到操作提交才清），从断点接着下（进度以磁盘为准：完成文件跳过、`.temp` 走 HTTP Range、暂存按 size/MD5 复用）。

**实时下载进度与速度**

- 下载进度按字节实时推进（增量差分包 / 全量同步 / 包式三条链统一），不再按文件完成跳变——大文件下载期间进度条纹丝不动的问题消除。
- 进度文案附带 EMA 平滑的实时下载速度（中英双语）。
- 预载/更新失败后重跑只补缺失部分：完好暂存文件按 size+MD5 核验跳过、`.temp` 断点续传（此前重跑会清空暂存从头重下）。

**系统托盘驻留与关闭按钮行为**

- 托盘图标常驻系统托盘（Linux 走 KDE 原生 StatusNotifierItem 协议、Windows 走 Shell_NotifyIcon，零第三方依赖）：左键唤回主窗口，菜单提供"显示主窗口 / 退出"。
- 设置页新增「关闭按钮」卡：点击关闭按钮 = 退出应用（默认，与此前行为一致）或 关闭窗口并驻留系统托盘（`closeAction` 设置项，games.json 可直接配置）。
- 驻留模式下点击关闭（含 alt+F4 / 系统关闭）只隐藏窗口：下载与依赖安装后台继续、背景视频暂停保活（托盘唤回即时续播）、窗口尺寸照常持久化；托盘"退出"走完整退出清理（停视频/取消依赖安装）。
- 依赖桌面环境提供 StatusNotifierItem 托盘：KDE 开箱即用；GNOME 需安装托盘扩展；无托盘环境请保持"退出应用"模式。

**缓存目录迁移到应用数据目录**

- 可重建缓存（FFmpeg 运行时、背景视频、背景图、唤取缓存）从配置目录迁移到应用数据目录（Linux `~/.local/share/yagl`、Windows `%LOCALAPPDATA%\yagl`，与 Wine prefix 同根）——配置目录只留 `games.json`；旧配置目录下的残留副本不再使用，可手动删除。

### 变更

**终末地更新改走官方增量补丁（2026-10-17，8.72 GiB 替代整包数十 GiB）**

- 更新时优先取官方 v2 增量载荷：带本地版本请求官方接口会返回「本地版本 → 最新版本」的增量计划（9 卷 ≈8.72 GiB，实测 1.5.3→1.6.4），逐文件按官方清单合成——差分条目是标准 HDiffPatch 单文件补丁（经 hpatchz 应用）、整文件条目直接落位、清单点名的"仅校验"文件确认在盘；随后删除官方废弃清单、清理载荷暂存。对比整包 58 GiB，下载量降到约 1/7。
- 载荷里的差分补丁包在 WinZip AES-256 加密的内层 zip 里（口令由接口下发的 `cd_key` 提供），.NET 的 zip 库不支持该加密，故自实现解密（PBKDF2-HMAC-SHA1 + AES-CTR，含认证码校验）。
- 不适用时自动回退整包：基文件缺失/内容与官方清单不符、分卷集异常、清单取不到、载荷自身不可用（条目读不出/产出与官方清单不符）等一律静默回退并清掉无用的载荷暂存，用户只会看到下载量变大，不会看到失败（载荷侧异常按 Warning 记录便于诊断）。
- 载荷里的静态文件（dll/exe 等）整文件覆盖游戏根；`config.ini` 按官方习惯写成 `config.ini.new`。

### 修复

**启动（2026-10-21）**

- 修复 Rider 调试/热重载会话下点「启动游戏」卡死直至超时：启动器继承的宿主 .NET 变量（`DOTNET_DiagnosticPorts` 等，已实锤诊断端口一项会让 Wine 内的 Windows .NET 运行时卡在 Main 之前）被原样传给 Proton → Wine → .NET 桥 helper，helper 永远不写 `STARTING`；现在 `SystemProcessRunner` 在正常的 `UseShellExecute=false` 派生路径中剥离宿主调试/热重载变量族（显式配置仍可覆盖；740 提权回退的 ShellExecute 不经过该剥离）。本地 Wine 探针：设置 `DOTNET_DiagnosticPorts` → 25 秒零状态，空值/未设置 → 0.2 秒 READY；上一批次迁移 C# helper 前 helper 是原生 C，不读该变量，故表现为“迁移后突然卡死”。

**界面**

- 详情页检测态修复：文件已存在（未登记、可启动）时不再显示空态引导卡——此前其安装指引与同屏的状态胶囊、启动按钮互相矛盾。
- umu 组件/Proton 检查与更新的进度、成败反馈改在启动卡内渲染（此前写进位置卡保存槽，反馈错位到错误区块）；位置卡与启动卡反馈槽互不清除，长反馈自动换行不再横向裁切。
- 收起态侧栏：指示点归位到选中行左缘（在选中高亮块内）；导航项图标居中修正（局部值此前压制了收起态样式）。
- 详情页兜底渐变不再盖住播放中的背景视频；关于页项目入口收敛为幽灵图标按钮、完整 URL 移入悬停提示，打开失败改警告 toast（不再沿命令链抛错）。
- 操作坞：启动/登记/预下载入口复位上一操作的进度（校验修复完成的 100% 卡不再残留到下一操作全程）；安装/修复确认在忙碌窗口不再静默丢弃。
- 离线回退真正生效：鸣潮网络错误以 `DownloadException` 形态逸出 `RefreshAsync` 过滤器（其自身契约要求任何失败落入离线态），离线状态、本地版本 chip 与缓存资产此前从不会出现。
- 背景图失败路径不再逸出为未观察任务异常：连接/响应头超时、畸形 `avares://` URI、无权限本地读。
- Proton 发行版「选择即保存」成功后复位脏标记（此前保存按钮保持点亮）。

**可靠性（内部）**

- **FFmpeg 供给（背景视频依赖，2026-09-24 真机定位）**：目录库改按依赖序预载、绑定就绪探针覆盖全部在用库（BtbN 构建无 RUNPATH、兄弟依赖须先加载——按 AutoGen 请求序加载必败，界面只剩静态海报）；系统库回退只认精确主版本（按库映射 avutil=61/avcodec=63 等，不再一律 `.so.63`）；tar 解包还原符号链接（此前压平成 0 字节文件）并重写存量扁平文件；下载改为 staging + 完成标记 + 原子落位，中断残留的半成品目录在绑定前删除（治愈「一次损坏永久锁死」），完成标记记录资产名、版本不符目录绑定前清掉（应用升级换资产不再绑到旧库）；校验/布局异常不再每次启动重下 60~75MB。
- **背景视频**：4K 帧位图改按 2 秒宽限回收（每张约 33MB，14 秒循环此前约漏 140MB/分钟）；`FrameSurface` 仅在挂载期订阅（修复重复订阅与脱挂泄漏）；播放器原生帧泄漏（循环结束/异常路径）收口；陈旧资产加载不再落进新会话（海报/静态窗口加代际复核）；停止时有界 join 解码任务（消除退出时 VAAPI destroy 报错）；解码器 EOF 排空（修复循环切点早跳 0.1~0.5 秒）；泊车会话重进不再续播陈旧区域/版本视频。
- **umu/Proton 容器**：放行根内向上相对符号链接（修复 dwproton 11.0-13 `default_pfx` 提取缺 DLL、游戏启动 0.5 秒即死）；Proton 升级删旧目录后自动重连悬空 builtin 链接（prefix 曾全灭、wine 退出码 53），升级改为先迁移后删旧、迁移不全保留旧树；运行时归档首段失败即清理（不再长期占用数百 MB 缓存）；上游版本/BUILD_ID 白名单校验（防路径穿越）、`SHA256SUMS` 缺条目拒装；替换安装改为先旁置再落位（长目录删除中崩溃不再毁掉可用安装）。
- **启动与稳定性**：Xft.dpi 同步的 stdout 读取任务异常不再逸出崩溃进程（.NET 10 下 `AggregateException` 不再派生 `SystemException`）；下载限速单位错配（Linux 时钟频率 1e9 被当 1e7、等待放大 100 倍）与文化无关解析（逗号小数地区 `0.5` 曾读成 `5`）修复，非有限与越界值拒收；Windows 悬空目录符号链接改用 `Directory.Delete` 删除；超时的 `xrdb` 子进程不再作为孤儿常驻。
- **解压与校验防线**：解包拒绝硬链接目标（同 inode 可写出沙箱）；累计解压体积上限 + 实际写出量与声明核对；清单校验把无权限读归入「不可读」（有界重下一轮，不再整轮中止）。
- **其他**：状态 toast 按语义去重（含最新版本号，类别/服务器往返不再重复弹同一条）；唤取全池视图的「距上一次五星」统计不再跨池混算——现按本义显示总抽数；界面语言归一仅对裸 `en`（`en-GB` 等区域码保持原义）。

**hpatchz 失败也回退直下（2026-10-03）**

- hpatchz 本身执行失败（如 2026-10-02 真机的退出码 109：供给构建读不了官方 krpdiff 头部，全组必败）时不再直接报错中止——与差分源缺失同族的组级回退生效：经目标版本全量清单解析该组产物直链后直下自救（size+MD5 校验 + 备份原子替换）；直下也不可得时错误信息同时保留补丁失败与回退失败两级原因，无解析缝的调用形态保持原报错。

**增量更新组级回退的误导性校验报错（2026-10-03）**

- 服务器版本已前移（或暂存版本尚未开放）时触发组级回退直下，此前会拿服务器当前版本的内容打旧版目标校验值、报误导性的「Checksum mismatch」；现在明确报出服务器当前版本与目标版本不一致，指引重新检测更新或重新预下载。

**鸣潮预下载（2026-09-29 预载窗口期不可用）**

- 官方 index.json 的 `predownload` 块不携带 `cdnList`（仅随 `default` 块下发），获取预载增量清单时误在该块上拒收——点「预下载下一版本」必报「Kuro index.json cdnList has no usable node」；现缺节点时回退 `default` 块的 cdnList。
- 增量清单资源文件的下载前缀误用差分入口的 `baseUrl`（差分包目录，CDN 上不存在资源文件），修复后按「条目 fromFolder > 清单第一个 fromFolder > 差分入口 baseUrl」回退——否则 cdnList 修复后仍会在首个文件 404。

**鸣潮增量更新 krpdiff 404 终局修复（2026-10-02）**

- 上项前缀修复仍会 404 的真根因：官方增量清单的 `resource` 列表同时登记直下文件与 krpdiff 差分文件（后者与 `groupInfos` 一一同名），krpdiff 被误当直下文件套上 zip/ 前缀——真机实测 38/38 全 404（用户报障的 404 URL 即此形态）；现 krpdiff 只经差分组表达（URL 用差分入口 baseUrl，实测全 206），并消灭了同文件下载两遍、应用时被搬进游戏目录两个连带缺陷。
- 差分组的 size/MD5 此前恒空（官方组层不带此二键）——现从 `resource` 同名条目回填，krpdiff 下载恢复 MD5 校验、进度总量不再漏算。
- 官方清单点名的废弃文件（`deleteFiles`，残留会被 UE 挂载覆盖新文件、热更卡死）此前被静默忽略——现应用更新时按清单删除，且在差分组与落位**之后**执行（官方清单存在废弃文件同时是组差分源的交叉，先删会让组差分永久失败且不可自愈）；删除失败即中止，重试安全。
- HTTP 404 等永久错误（4xx，除 408/429）不再按网络瞬态重试三次——单次终止并在错误信息携带状态码。

**应用预下载假死与增量更新自愈（2026-10-02）**

- 点「应用预下载 / 更新」重入已部分应用的安装时 UI 整体假死数分钟（重入校验对全部已应用组产物做同步 MD5，真机实测合计 73GiB 跑在 UI 线程）——现应用链入口让位线程池，组校验与暂存核验走异步流式 MD5（可取消），重入校验阶段显示「校验中」进度。
- 差分源缺失的组不再直接失败：按目标版本全量清单解析该组产物直链后直下自救（size+MD5 校验 + 备份原子替换），被此前失败尝试损坏的安装可自愈；查无直链时保留「全量更新」指引。
- 重跑预下载时，暂存核验（size+MD5）的逐块读取实时计入进度与速度——与真实下载同一进度管道，暂存齐备的重跑不再 0→100 闪过。

**终末地整包更新解压失败（2026-10-17 用户报障）**

- 更新下载完成后必报「Failed to extract Beyond_Release_…official.zip.001: End of Central Directory record could not be found」：官方 `pkg.packs` 是**同一归档的字节分卷**（1.6.4 实测 59 卷 ≈58GiB，`.zip.001…059`），此前把每一卷当独立 zip 解压——中央目录与 EOCD 只在末卷，逐卷解压必然失败。现由 `SplitVolumeStream` 把各卷呈现为一个连续可寻址流整体解压一次（不落盘拼接副本，数十 GiB 分卷不产生等量临时文件），完整安装与预下载/应用两条链同源生效；已下载的分卷在重试时按 size+MD5 复用，不重下。
- 分卷序号不连续或多基名（服务器数据自相矛盾）现在提前拒收并给出说明——不再让用户先下几十 GiB 才在解压阶段撞上无分类的 EOCD 报错。

**修复：旧版本预下载的分卷暂存不再作废（2026-10-17）**

- 用旧版本启动器预下载的暂存清单没有分卷标记：应用阶段会退回逐卷解压（正是本次报障的 `End of Central Directory` 形态），而此刻几十 GiB 已下载完。现在按与渠道同一规则复算认定分卷并重排卷序，零下载救回这段存量；像分卷但序号不连续则在解压前明确报错。

**终末地更新后清理旧版残留文件（2026-10-17 用户点名「要清理旧版残留，不然空间占用太大了」）**

- 包式更新只解压覆盖、不删旧文件，上一版残留随版本累积（实测 1.5.3→1.6.4 有 47.72 GiB 旧 VFS 分块不在 1.6.4 归档里）。现在更新落地后按**官方废弃清单**清理：带本地版本请求官方接口会返回"本地版本 → 最新版本"的更新补丁，其中的 `delete_files.txt` 即官方点名要删的文件（1.6.4 实测 324 条）。清单住在约 49 GiB 的补丁包里，读取只经 HTTP Range 分段取尾部/中央目录/该条目（实测 4 个请求 ≈ 24 KB，绝不整包下载）。
- 清理的防线：只删清单点名且不在本次解压产物里的文件（官方清单与新归档实测零重叠，仍按"产物优先"兜底）；路径必须落在安装目录内且不穿过链接；状态目录 `.yagl` 不删；删除后回收腾空目录；单条失败只记日志继续。清单取不到（渠道不支持/服务器已前移/网络失败）不影响更新，只是残留保留到下次。
- 顺带修复了真机复现的一次瞬态网络失败会让清理静默失效：远程分段读取对 5xx/408/429/连接中断做线性退避重试（4xx 其余判永久失败），并在服务器忽略 Range 回整包时拒绝缓冲（体量闸）。

**终末地启动校验：诱饵父进程桥（Linux，免官方启动器，2026-10-18）**

- 终末地 1.6.3+ 启动时会校验**直接父进程**（镜像名 + `WinVerifyTrust` 签名 + 签名者证书指纹白名单，实现在游戏目录 `OneChannelLC.dll`）；原生 umu 链直启时父进程是容器侧 `umu.exe`，会被拒绝（弹"请通过官方启动器打开游戏"、退出码 5）。现在启动链内置**诱饵父进程桥**：把游戏目录里鹰角签名的可执行文件（`PlatformProcess.exe` → `UnityCrashHandler64.exe` → `Endfield.exe`，取首个存在者；与官方启动器 `Games.exe` 同一张签名证书）复制为诱饵 `Games.exe`（应用数据目录的桥目录，2026-10-20 起；此前在游戏目录 `.yagl/bridge/`），以挂起状态充当游戏进程的父进程——wine 侧由随包嵌入的 helper 编排（创建挂起诱饵 → 以父进程属性拉起游戏 → 游戏退出后回收诱饵），**零下载、不安装官方启动器、不改游戏文件**。
- 由 `launch.decoyParent` 开关控制（内置样例的终末地条目已开启；存量配置由 schemaVersion 6 一次性迁移补上）；候选文件缺失或准备失败静默回退直启。父进程名/签名/指纹三层判据与三种失败码（10100/10101/10102）均经真机对照实验与通过态实录验证，详见 `artifacts/bugs.md`。

**终末地增量链补全（2026-10-18）**

- 预下载也改走 v2 增量：预下载窗口内优先取官方增量计划，只下 8.72 GiB 载荷（此前预下载仍整包 58 GiB）；应用时复用已下载荷（零重下），载荷不适用则清掉暂存并重走常规更新（不会把下载时的目标版本当成"当前最新"落盘）。
- 预下载窗口期的增量目标按 `patch.version` 匹配（复审修正）：窗口内线上版本仍是旧版、patch 节点描述的是预下载目标，此前守卫只认线上版本 → 增量计划恒取不到、静默退回 58 GiB 整包；清单 version 仍逐字符核对兜底。
- 增量预下载下载失败回退整包时只清增量侧（载荷 + `delta.json` + 增量标记清单），保留上轮整包暂存 `.yagl/predownload/packages` 按 size/MD5 复用（复审修正）：此前回退会整删预下载目录，把数十 GiB 已暂存内容作废重下。
- 应用前空闲空间预检：按"载荷 + 缺失新内容 + 最大单节点瞬态"估算，不足时给出所需/可用空间与"已下载载荷可复用"的提示（不回退整包——整包只会要得更多）。
- 清单"仅校验"条目补内容级核对（尺寸 + md5）：尺寸相同但内容损坏/被换过的文件不再静默放行，判为不适用回退整包。
- 大条目（1.6.4 实测 1.2 GiB）流式解密落盘：不再整块进内存（约 2 倍峰值）。
- 补丁工具不可用（缺失/自动供给失败/进程起不来）与"载荷不可用"分家：保留已下载的 8.7 GiB 载荷、报可重试错误，修复工具后重试零重下（此前会清掉载荷并静默回退整包）。
- 零校验登记不再冒充"已是最新版本"：检测到现有文件即登记时状态显示「已登记版本（未校验）——建议执行校验修复」，执行校验修复后恢复为已验证（半更新安装不再被掩盖）。
- 新增应用级日志文件 `logs/yagl-yyyyMMdd.log`（保留 14 天，写失败静默）：更新/预下载等事件与异常尾巴有了可查的落盘记录，事故复盘不再只能靠文件 mtime。
- 界面剩余离线间距/内边距收敛到 4pt 栅格：卡片内部、覆盖层按钮行、toast 卡片间隙与内边距同步齐平；下载/更新进行中「启动游戏」按钮禁用（此前只有命令内早退、外观仍可点），坞内忙碌进度条改走插画上令牌（亮色主题不再丢轨道），禁用按钮的内容图标随前景一起下沉。

**终末地诱饵父进程桥：黑窗修复 / helper 迁移 C# / Windows 直启接入（2026-10-20）**

- 修复点「启动」时弹出的全屏黑窗：桥 helper 此前是 console 子系统 PE，经 Proton 容器启动时 wine 为它开出 `conhost.exe` 黑窗（真机 `hyprctl clients` 复现 `class='conhost.exe'`、标题含 helper 路径；容器内 helper stderr 被吞，黑窗纯属副作用）。helper 迁移为 **C# `WinExe`（GUI 子系统）自包含单文件**（`tools/wine-parent-bridge/`，源码随仓库、产物不再提交，由 Core 构建目标发布并嵌入），真机链路（`_v2-entry-point → proton waitforexitandrun → helper`）实测无任何 conhost 窗口（合成夹具与真实游戏各一次实测），父进程归属与诱饵回收不变。
- 桥工作目录从 `<安装目录>/.yagl/bridge/` 迁到**启动器数据目录**（Linux `~/.local/share/yagl/bridge/<游戏id>`、Windows `%LOCALAPPDATA%\yagl\bridge\<游戏id>`，即数据目录下的 `bridge/<游戏id>/`）：游戏安装目录恢复只读（仅作为签名候选来源），旧目录 best-effort 清理且不触碰 `.yagl` 状态文件。
- **Windows 直启接入同一 helper**：`launch.decoyParent` 不再仅限 Linux 原生 umu 链——Windows 直接运行 `{exe}` 时同样以挂起诱饵为父进程拉起游戏；`commandTemplate` 未直接指向游戏 exe、候选缺失或桥目录不可写时抛可操作错误（Windows 上静默直启必被游戏拒绝）。配套新增 Windows 原生编排测试（Linux 上 Skip）与 .NET Wine 自检器（取代旧 shell 脚本与 C 夹具）。
- helper 的 Wine 兼容坑一并固化：`UpdateProcThreadAttribute` 的 `lpValue` 句柄变量必须保活到 `CreateProcessW` 返回（Wine 存指针而非拷贝值，提前释放 → `ERROR_INVALID_HANDLE (6)`）；NativeAOT 跨 OS 交叉编译不可用，helper 采用自包含单文件发布。详见 `docs/PITFALLS.md`。
- 复审修复（2026-10-20）：Core 的 helper 构建目标显式钉死 `RuntimeIdentifier=win-x64;SelfContained=true` 并在目标内重新 Restore——此前外层 `-p:RuntimeIdentifier=linux-x64` 会压过 helper 工程自己的 RID，发布出无 `.exe` 的 ELF，嵌入资源缺失使干净发布失败；CI 新增 Linux 干净发布冒烟（断言 helper 为 Windows PE 且已嵌入 Core.dll）。
- 诱饵内容与候选一致时跳过覆盖：Windows 上已挂起的诱饵进程会锁住 `Games.exe`，第二次启动或 helper 被强杀留下孤儿诱饵不再因覆盖失败被报成"桥目录不可写"。
- Windows 直启与 Linux 原生 umu 链统一改走 **helper 启动状态文件**：helper 在游戏进程创建后写 `READY`，创建前失败写 `ERROR:<退出码>:<消息>`；launcher 等待 READY/ERROR 而不是看 helper 退出码——既能在两个平台把桥启动失败显示为可操作错误，也不会把游戏自身退出码（如启动校验拒绝的 5）误报成桥故障。等待按**两段计时**（2026-10-21）：helper 写出首个状态前用启动上限（10 s，Linux 链含 Proton/umu 容器冷启动），helper 开始执行后用 READY 上限（10 s，游戏进程创建）；两段超时提示分开（helper 未启动 / 已启动但游戏进程未创建）。2026-10-21 真机彻底冷启动实测首个桥状态 READY 4062 ms（10 秒预算余量约 5.9 秒）。超时提示同时给出启动日志与同目录应用日志路径（first status / startup timeout 诊断在后者）。之前真机观察到的 74 s 是点击到手动关闭旧实例的时长，属于 READY 之后游戏自身的单实例等待，不在 launcher 的状态等待内（旧的单一 30 s 上限不会等 74 s）——两段超时只约束 helper 启动与游戏进程创建。
- .NET Wine 自检器只在 Core 构建产物不旧于 helper 源码时复用，否则现场发布，避免改完 helper 后自检静默 PASS 旧二进制。

**其他**

- 关于页第三方许可清单修正：HDiffPatch 与 FFmpeg.AutoGen 标注 MIT（对照上游 LICENSE / NuGet 许可文件核验），补上 Avalonia.Fonts.Inter（MIT 包，Inter 字体 OFL-1.1）。

---

### English · 0.1.3 — unreleased

A feature release after 0.1.2: a full UI overhaul (detail-page identity,
sidebar tooltips and short statuses, danger-styled destructive actions,
a token contrast gate, type-scale and spacing grids); dependency
installs on Linux (bundled Source Han Sans); Steam-aligned custom
launch options (%command% placeholder) plus a new Feature toggles
section; system tray dwelling with a close-button behavior setting
(quit the app / close the window and dwell in the tray, with downloads
and dependency installs kept running); resource quality tier selection;
real-time byte-granular download progress with a speed readout;
automatic hpatchz provisioning and the definitive fix for the Wuthering
Waves krpdiff incremental-update chain, plus the split-volume extraction
fix for Endfield package updates plus stale-file cleanup and the delta update path; the Endfield launch check is handled by a decoy parent bridge (no official launcher needed; Windows direct launch was added on 2026-10-20, and the bridge helper was ported to a C# GUI-subsystem single file to eliminate a spurious console window). 1572 tests green (measured 2026-10-22).

**Added — UI overhaul**

- Detail page: identity moves to the window title ("<Game> - App" on game pages, following the language switch) and the sidebar selection - no self-drawn title (official backdrops carry a burned-in logo); an empty-state guide card (channel / latest version / server count / install directory + guidance) shows when the game is uninstalled and not launchable, and the launch button hides (Install becomes the main action).
- Sidebar: collapsed icon-only items reveal the full game name on hover; statuses use short copy (the English detected status no longer truncates into a broken stem); the collapsed indicator dot sits inside the selection highlight.
- Destructive actions get their own visual class: the repair-reinstall confirm (red text on a near-solid card) and the Proton update confirm (danger button in the error card, light red hover wash) no longer look like the constructive primary; the launch-error overlay swaps primary/secondary with retry-ability; the update confirm states the destructive verb up front ("Replace old & update").
- Readability gate: a new theme-token contrast gate (white-on-accent, badges, version-chip gold, dock captions, gacha rarity and error text, composited over worst-case translucent backdrops, >=4.5:1 in both themes); light/dark accents darkened per the gate result (hover one step further); gacha rarity colors collected into themed tokens.
- Type and spacing: 17 distinct font sizes collapse to 9 (12/13/14/15/16/18/20/24/28); all axaml spacing aligns to a 4-step grid (>=8 is a multiple of 4, <8 is even).
- Interaction consistency: the settings-page install-root row gets an inline browse button (matching the game-settings location card); the launch-settings save button is dirty-aware (lit only while the draft differs, reset after save/rollback); new toasts are dropped while a launch-error overlay is up; the About page gains a project-home entry (ghost GitHub icon, full URL on hover, failures surface as a warning toast).

**Added — dependency installs (Linux)**

- New "Dependencies" card in game settings (Linux only): manually install optional components into the current game's Wine prefix, with a per-item status dot, install/reinstall button, progress row and a dedicated feedback slot.
- First bundled component: CJK fonts (Source Han Sans, Adobe release 2.004R, md5+size pinned) - cached download (XDG cache, resumable, shared across games), defensive extraction, copy into windows/Fonts, and a single wine reg import landing 28 font registrations plus 51 replacement entries mapping CJK system font names (SimSun, Microsoft YaHei, MS Gothic, ...) to Source Han Sans - fixing missing-glyph tofu boxes under Wine.
- The install target is resolved up front (umu-bundled wine vs system wine) with a prefix health precheck; an uninitialized prefix (game never launched) or a missing Proton yields an actionable hint instead of an error; installs are cancellable and never leave a completion marker on failure.

**Added — launch-card Feature toggles**

- New "Feature toggles" section on the game-settings launch card (Linux only): Wayland driver (PROTON_USE_WAYLAND), DLSS upgrade (PROTON_DLSS_UPGRADE + PROTON_ENABLE_NVAPI) and Proton log (PROTON_LOG + PROTON_LOG_DIR), persisted via the save button; injected on both the template direct-launch and the native umu chains (feature keys win over user env vars).
- The umu chain also sets STEAM_COMPAT_LIBRARY_PATHS to the game's install directory.
- The environment editor shows user variables only: managed keys (PROTONPATH, runner-generated entries, ...) stay out of the box; saving merges managed + user with user values winning; switching the Proton flavor persists instantly.

**Added — cache directories moved to the app-data directory**

- Rebuildable caches (FFmpeg runtime, backdrop video, backdrop images, gacha API cache) move from the config directory to the app-data directory (Linux ~/.local/share/yagl, Windows %LOCALAPPDATA%\yagl, the same root as the Wine prefixes) - the config directory now holds games.json only; leftover copies under the old config directory are unused and can be deleted manually.

**Added — custom launch options: rename & Steam-style lexing**

- The "Environment variables" box is renamed "Custom launch options", with an ⓘ icon next to the label showing the fill-in rules on hover (Material info glyph, solid disc with an inverted glyph).
- The input format now matches Steam launch options: whitespace (including newlines) separates multiple `KEY=VALUE` entries, and values containing spaces must be quoted (e.g. `MAP="coast 11"`) — unquoted in-value spaces are no longer swallowed and spaces around `=` are no longer tolerated; double/single quotes, backslash escapes and quoted newlines are supported.
- The settings toggle group previously named "Launch options" is renamed "Feature toggles" (avoiding the name clash with the launch options box).

**Added — Steam `%command%` placeholder in custom launch options**

- Omitting `%command%` in the box appends it automatically at the end: `KEY=VALUE` entries are injected as environment variables of the launched process — consistent with the Steam launch options format, no placeholder needed.
- Writing an explicit `%command%` line makes everything after it game command-line arguments appended after the game executable (e.g. `%command% -dx11`), effective on both the template direct-launch and native umu chains; games.json can also configure it via the `launch.arguments` array.
- Quoting/escaping lexing is unchanged; `%command%` may appear at most once. Bare tokens (a common misconception when trying to pass game arguments), an unclosed quote in the arguments area and a duplicated placeholder each get a dedicated error message, and the bare-token one points straight at the `%command%` syntax (Chinese and English).

**Added — automatic hpatchz provisioning (no manual install for Wuthering Waves incremental updates)**

- When applying incremental updates, if hpatchz is not on PATH the launcher automatically downloads a community-verified build (Wuthering Waves krpdiff is incompatible with every open-source HDiffPatch build - measured on official v4.8.0/v5.1.3 and master, all exit 109; the provisioned asset is the hpatchz.exe packaged by the ww-manager community, MD5-verified, output verified against the official manifest). On Linux it runs through wine (system wine or one bundled with an installed Proton, isolated WINEPREFIX); a self-provided binary always takes precedence and runs natively.

**Added — resource quality tier and per-tier downloads (Wuthering Waves, extended 2026-10-19)**

- New "Resource quality tier" dropdown in game settings (HD / SD / UHD / follow in-game settings, shown for Wuthering Waves only): passed to the game as the official `-krqlv=<hd|sd|uhd>` launch argument on both the umu and direct launch chains; configurable directly in games.json via `launch.resourceQualityTier` (whitelist-validated).
- Since 2026-10-19 the tier also selects the download source: full sync / verify-repair build the manifest for the chosen tier (other tier directories removed, target tier pack added), while incremental updates take the tier-independent base delta and the full repair round after the update fills the tier pack; tier-pack downloads require the server's official tier index (`servers[].options.resourcePackIndexUrl`, given for the Wuthering Waves CN server in the built-in sample; the Global/Bilibili servers have none, so the tier is a launch argument only there, as the settings page notes). Existing configs get the key from the `schemaVersion 7` migration.
- Tier coexistence: switching tiers never deletes old tier files - neither the sync cleanup nor the incremental delete list touches preserved tier directories (the channel crops the delete list per tier, and the consumer keeps an independent safeguard); the settings page "Clean other tiers" previews the reclaimable size and target tiers, then deletes only files named by each tier's manifest that resolve inside that tier directory, plus leftover `.temp` partial downloads (with a warning when the selected tier is not fully on disk).
- With the game installed and the on-disk tier differing from the selected one, the main button becomes "Download resource pack" (update + pack in one) and the status dot uses the action colour instead of green.

**Added — download pause and resume (2026-10-19)**

- A "Pause" button in the busy area of the dock cancels the running operation and keeps everything downloaded and staged (no version written, no error, no cleanup); it works in any phase.
- Cross-session resume: `.yagl/download-session.json` is written when an operation starts (with its kind and game/server attribution) and cleared only on success, so reopening the app shows "Resume download" and dispatches back to the paused action (a paused pre-download stays a pre-download; a finished download resumes the apply step, whose staged manifest is kept until the operation commits) and continues from the break (progress lives on disk: finished files are skipped, `.temp` files resume via HTTP Range, staged files are reused by size/MD5).

**Added — real-time download progress & speed**

- Download progress now advances per byte across all three download chains (incremental patches / full sync / package installs) instead of jumping per completed file — no more frozen progress bars during large-file downloads.
- The progress line carries an EMA-smoothed speed readout (Chinese and English).
- Failed pre-downloads/updates resume on rerun: intact staged files are verified (size+MD5) and skipped, `.temp` files resume via HTTP Range (previously a rerun wiped the staging directory and restarted from scratch).

**Added — system tray dwelling & close-button behavior**

- A tray icon always dwells in the system tray (KDE-native StatusNotifierItem protocol on Linux, Shell_NotifyIcon on Windows, zero third-party dependencies): left click restores the main window; the menu offers "Show main window / Quit".
- New "Close Button" card in Settings: clicking close quits the application (default, unchanged behavior) or closes the window and dwells in the system tray (`closeAction` setting, also configurable directly in games.json).
- In dwelling mode, closing (including Alt+F4 / system close) only hides the window: downloads and dependency installs keep running in the background, the backdrop video pauses with keep-alive (instant resume on restore), and window geometry is persisted as usual; tray "Quit" runs the full shutdown cleanup (stop videos / cancel dependency installs).
- Requires a StatusNotifierItem tray from the desktop environment: works out of the box on KDE; GNOME needs a tray extension; keep "quit" mode on tray-less environments.

**Fixed — UI**

- The detail page no longer shows the empty-state guide card in the detected state (files present, not registered, launchable) - its install guidance contradicted the status chip and the launch button on the same screen.
- umu component/Proton check and update progress and results now render inside the launch card (they were written into the location card's save slot and showed up in the wrong section); the two feedback slots never clear each other and long feedback wraps instead of clipping.
- Collapsed sidebar: the nav dot anchors inside the selected row's highlight (previously at the window edge, detached from the row); nav icons are actually centered (local values had outranked the collapsed override).
- The detail-page fallback gradient no longer covers a playing backdrop video; the About project entry collapses into a ghost icon button with the full URL in a tooltip, and open failures raise a warning toast instead of propagating along the command chain.
- The operation dock resets the previous operation's progress when Launch/Register/Predownload starts (a finished verification's 100% card no longer lingers through the next operation); install/repair confirmations are no longer silently dropped during the busy window.
- Offline fallback actually runs now: Kuro network errors surfaced as DownloadException escaped RefreshAsync's filter (whose own contract requires falling back to the offline state), so the offline status, local version chip and cached assets never executed.
- Background-image failure shapes no longer escape as unobserved task exceptions: connect/header timeouts, malformed avares:// URIs and permission-denied local reads.
- The Proton flavor's instant save resets the dirty flag (the save button used to stay lit).

**Fixed — reliability (internal)**

- **FFmpeg provisioning (the backdrop-video dependency; root-caused on a real machine 2026-09-24)**: directory libs now preload in dependency order and the bind readiness probe covers every library in use (BtbN builds carry no RUNPATH and depend on sibling sonames - loading in AutoGen request order always failed, leaving the static poster); the system fallback accepts exact majors only (a per-library map: avutil=61/avcodec=63 etc., instead of a blanket .so.63); tar extraction restores symlinks (previously flattened into 0-byte files) and re-extracts legacy flattened dirs; downloads go through a staging dir + completion marker + atomic move, and unmarked half-extracted dirs are deleted before any bind (healing the "one interrupted download locks the binding forever" state); the completion marker records the asset name and version-mismatched dirs are removed before binding (an app upgrade no longer binds old libs); checksum/layout failures no longer re-download 60-75MB every session.
- **Backdrop video**: 4K frame bitmaps retire through a 2s grace queue (about 33MB per bitmap; a 14s-looping 4K backdrop leaked ~140MB/minute); FrameSurface subscribes only while attached (double-subscribe and detached-surface leaks fixed); native frame leaks (loop-end and exception paths) are closed; stale asset loads can no longer land after a newer load (poster/static windows re-check the generation); Stop bounded-joins the decode tasks (exit-time VAAPI destroy errors gone); the decoder drains its reorder buffer at EOF (seam cuts jumped 0.1-0.5s early); re-entering a parked session no longer resumes a stale region/version video.
- **umu/Proton containers**: in-root relative-upward tar links are allowed again (dwproton 11.0-13 default_pfx extraction lost every DLL and launches died in 0.5s); after a Proton upgrade deletes the old tree, dangling builtin links are relinked (prefixes had died wholesale, wine exit code 53) and upgrades now migrate first, keeping the old tree when migration is incomplete; a first-segment runtime download failure cleans its archive (no more hundreds of MB rotting in the cache); upstream version/BUILD_ID texts are whitelisted (path-traversal segments rejected) and a missing SHA256SUMS entry refuses the install; replacements rename the old tree aside and move the new one in (a crash mid-delete no longer destroys the usable install).
- **Startup & stability**: a faulted stdout read task in the Xft.dpi sync path can no longer escape its catch and crash startup (on .NET 10 AggregateException no longer derives from SystemException); the download speed limiter's unit mismatch (Linux QPC = 1e9 treated as .NET ticks, inflating waits 100x) and culture-sensitive parsing (comma-decimal locales read 0.5 as 5) are fixed, and non-finite or out-of-range values are rejected; Windows dangling directory symlinks delete via Directory.Delete; a timed-out xrdb query no longer leaves a hung child process behind.
- **Extraction & verification defenses**: archive extraction rejects hard-link targets (same-inode writes escaped the sandbox); a cumulative extraction-size cap plus a written-vs-declared size check; manifest verification maps permission-denied reads to "unreadable" (one bounded re-download round instead of aborting the whole verification).
- **Other**: status toasts dedupe by semantics (keys include the latest version, so category/server round-trips stop re-toasting the same update); the gacha all-pools view's "Since Last 5-Star" stat no longer mixes pools into a meaningless number - it shows total pulls per the property's own spec; language normalization applies to bare "en" only (en-GB and friends keep their meaning).

**Fixed — hpatchz failures now fall back to direct downloads too (2026-10-03)**

- When hpatchz itself fails (e.g. the real-machine exit code 109 on 2026-10-02: the provisioned build could not read the official krpdiff header, failing every group), the update no longer aborts outright — the group-level fallback, previously reserved for missing diff sources, now kicks in: the group's outputs are resolved against the target version's full manifest and downloaded directly (size+MD5-verified, atomic backup replacement). If the direct download fails as well, the error carries both causes (patch failure and fallback failure); callers without a resolver keep the original error.

**Fixed — misleading checksum error in the incremental group fallback (2026-10-03)**

- When the server has moved past the staged version (or the preloaded version is not publicly available yet), the group fallback used to fetch the server's current content against the old target's checksum and fail with a misleading "Checksum mismatch"; it now names both versions and tells the user to re-check for updates or re-run the predownload.

**Fixed — Wuthering Waves predownload (broken during the 2026-09-29 predownload window)**

- The official index.json `predownload` block carries no `cdnList` (it is only published on the `default` block), yet the predownload incremental-manifest fetch rejected on that block — clicking "Predownload next version" always failed with "Kuro index.json cdnList has no usable node"; the default block's cdnList is now used as a fallback.
- Resource-file URL prefixes in incremental manifests wrongly used the patch entry's `baseUrl` (a patch-package directory with no resource files on the CDN); they now fall back per "entry fromFolder > first fromFolder in the manifest > patch-entry baseUrl" — otherwise the cdnList fix would still 404 on the first file.

**Fixed — Wuthering Waves incremental update krpdiff 404 (definitive, 2026-10-02)**

- The real root cause behind the prefix fix above still 404-ing: the official incremental manifest's `resource` list registers both direct files and krpdiff patch files (the latter one-to-one with `groupInfos`), and krpdiff entries were mistakenly treated as direct files with the zip/ prefix — 38/38 URLs 404 on the live CDN (exactly the user-reported 404 URL). krpdiff is now expressed solely through patch groups (URL from the patch-entry baseUrl, all 206 when measured), which also eliminates downloading each patch twice and moving it into the game directory at apply time.
- Patch-group size/MD5 were always empty (the official group entries carry neither) — now backfilled from the same-name `resource` entry, restoring MD5 verification for krpdiff downloads and correct progress totals.
- Obsolete files named by the manifest (`deleteFiles` — leftovers get mounted by UE over new files and stall hot-updates) were silently ignored — they are now deleted during the update, **after** the patch groups and staged-file placement (the official manifest has obsolete files that double as diff sources for later groups; deleting them first makes those groups permanently fail); a failed delete still aborts, safely retryable.
- Permanent HTTP errors (4xx other than 408/429) no longer retry three times as if transient — a single attempt terminates with the status code in the message.

**Fixed — apply-predownload UI freeze & incremental update self-healing (2026-10-02)**

- Clicking "Apply pre-download / Update" on a partially applied install froze the whole UI for minutes (the re-entry check hashed every already-applied group's output with blocking MD5 - 73 GiB measured on the user's machine, on the UI thread). The apply chain now yields to the thread pool on entry, the re-entry group check and staging verification use streaming async MD5 (cancellable), and the verification stage shows a "Verifying" progress phase.
- Groups whose diff sources are missing no longer fail outright: their outputs are resolved against the target version's full manifest and downloaded directly (size+MD5-verified, atomic backup replacement), so installs damaged by earlier failed attempts self-heal; unresolvable paths keep the "use the full update" guidance.
- Rerunning a pre-download now streams the staging verification (size+MD5) reads into the same progress/speed pipeline as real downloads - a fully staged rerun no longer jumps from 0 to 100.

**Changed — Endfield updates now use the official delta payload (8.72 GiB instead of tens of GiB, 2026-10-17)**

- Updates prefer the official v2 delta: requesting the update with the local version returns a "local -> latest" delta plan (9 volumes, ~8.72 GiB measured for 1.5.3 -> 1.6.4), and every file is produced from the official manifest - delta entries are standard HDiffPatch single-file patches (applied with hpatchz), full-file entries are placed directly, and manifest entries that only need verification are confirmed on disk; the official obsolete list is then applied and the payload staging is cleaned up. That cuts the download to roughly a seventh of the full package.
- The per-file patches are wrapped in WinZip AES-256 encrypted inner zips (the password is the `cd_key` from the API); .NET's zip classes cannot read that encryption, so decryption is implemented in-house (PBKDF2-HMAC-SHA1 + AES-CTR with auth-code verification).
- Falling back is automatic whenever the delta does not apply (missing or modified base files, broken volume set, unreachable manifest, unusable payload such as an unreadable entry or output that does not match the official manifest): the launcher silently falls back to the full package and drops the now-useless payload staging, so the only visible effect is a larger download (payload-side problems are logged as warnings for diagnostics).
- Static files in the payload (dlls/exe/...) overwrite the game root; `config.ini` is written as `config.ini.new` per the official convention.

**Endfield launch check - suspended decoy parent bridge (Linux, no official launcher required, 2026-10-18)**

- Since 1.6.3, Endfield verifies its **direct parent process** (image name + `WinVerifyTrust` signature + signer certificate fingerprint whitelist, implemented in the game's `OneChannelLC.dll`); a direct launch through the native umu chain used to present the container-side `umu.exe` as the parent and was refused (dialog "please open the game through the official launcher", exit code 5). The launch chain now ships a **decoy parent bridge**: a Hypergryph-signed executable already present in the game directory (`PlatformProcess.exe` -> `UnityCrashHandler64.exe` -> `Endfield.exe`, first hit; same signing certificate as the official launcher's `Games.exe`) is copied as the `Games.exe` decoy (into the app-data bridge directory since 2026-10-20, previously `.yagl/bridge/` inside the game directory) and created suspended as the game process's parent - the bundled helper orchestrates this inside wine (create the suspended decoy -> start the game with the parent attribute -> reap the decoy when the game exits), with **zero downloads, no official launcher install and no game files modified**.
- Gated by `launch.decoyParent` (enabled for Endfield in the built-in sample; existing configs receive it via the one-time schemaVersion 6 migration); missing candidates or preparation failures silently fall back to a direct launch. All three conditions (parent name / signature / fingerprint) plus the three failure codes (10100/10101/10102) were verified by controlled experiments and a passing real-machine run - see `artifacts/bugs.md`.

**Fixed — launch hangs under Rider debug/hot-reload sessions (2026-10-21)**

- Fixed the launch button hanging until timeout when the launcher runs under a Rider debug/hot-reload session: host .NET variables inherited by the launcher (notably `DOTNET_DiagnosticPorts`; a local Wine probe showed the Windows .NET helper blocking before `Main` when it is set) were passed through Proton -> Wine -> the .NET bridge helper, which then never wrote `STARTING`. `SystemProcessRunner` now strips the host debug/hot-reload variable family (`DOTNET_DiagnosticPorts` / `DOTNET_STARTUP_HOOKS` / `DOTNET_HOTRELOAD_NAMEDPIPE_NAME` / `DOTNET_MODIFIABLE_ASSEMBLIES`) from child processes on the normal `UseShellExecute=false` path before appending explicit configuration (the 740 elevation fallback uses `UseShellExecute=true` ShellExecute and does not go through this stripping). Local Wine probe: variable set -> no status for 25 s; unset/empty -> `READY` in 0.2 s. The previous batch's C# helper migration is why this only started after 2026-10-20: the old native C helper ignored .NET variables.

**Fixed — Endfield decoy parent bridge: console-window fix, C# helper, Windows direct launch (2026-10-20)**

- Fixed the full-screen black window that appeared when launching Endfield: the bridge helper used to be a console-subsystem PE, and Wine spawned a `conhost.exe` window for it inside the Proton container (reproduced on a real machine via `hyprctl clients`: `class='conhost.exe'`, title containing the helper path; the helper's stderr is swallowed inside the container, so the window was pure side effect). The helper is now a **C# `WinExe` (GUI subsystem) self-contained single file** (`tools/wine-parent-bridge/`, sources in-repo, no committed binary; the Core build target publishes and embeds it). The real chain (`_v2-entry-point -> proton waitforexitandrun -> helper`) shows no conhost window in both a synthetic fixture and a real game launch, with parent attribution and decoy reaping unchanged.
- The bridge working directory moved from `<installDir>/.yagl/bridge/` into the launcher data directory (Linux `~/.local/share/yagl/bridge/<gameId>`, Windows `%LOCALAPPDATA%\yagl\bridge\<gameId>`): the game install directory is read-only again (used only as the signed-candidate source), and the legacy directory is removed best-effort without touching `.yagl` state files.
- **Windows direct launch now uses the same helper**: `launch.decoyParent` is no longer Linux-native-umu-only - on Windows the direct `{exe}` run also starts the game with a suspended decoy as its parent. A `commandTemplate` that does not point straight at the game exe, missing candidates or an unwritable bridge directory now raise an actionable error (a silent direct launch would always be rejected on Windows). Adds a native Windows orchestration test (skipped on Linux) and a .NET Wine verifier (replacing the old shell script and C fixture).
- Review fixes (2026-10-20): the Core helper build target now pins `RuntimeIdentifier=win-x64;SelfContained=true` and re-runs Restore inside the target - an outer `-p:RuntimeIdentifier=linux-x64` used to override the helper project's own RID, producing an extension-less ELF and breaking the embedded resource on a clean publish; CI adds a Linux clean-publish smoke that asserts the helper is a Windows PE and is embedded in Core.dll.
- The decoy is no longer overwritten when its content already matches the candidate: on Windows a suspended decoy process locks `Games.exe`, so a second launch (or an orphaned decoy left by a force-killed helper) no longer fails the copy and gets reported as an unwritable bridge directory.
- Windows direct launch and the Linux native umu chain now share a **helper startup status file**: the helper writes `READY` once the game process exists and `ERROR:<code>:<message>` on a pre-launch failure; the launcher waits for READY/ERROR instead of interpreting the helper's exit code, so bridge startup failures are actionable on both platforms without misreporting the game's own exit code (e.g. the validation rejection 5) as a bridge fault. The wait is **two-phase** (2026-10-21): a 10 s startup budget (including Proton/umu container cold start on Linux) before the helper writes its first status, then a 10 s READY budget for game-process creation; the two timeout paths report different errors (helper did not start / helper started but the game process was not created). A real fully-cold launch on 2026-10-21 reached the first bridge status READY in 4062 ms (about 5.9 s of headroom under the 10 s budget; READY already includes helper startup plus game-process creation). Timeout messages also point at the application log in the same directory, where the first-status / startup-timeout diagnostics are written. The earlier 74 s observation measured the time until the previous instance was closed manually; that wait belongs to the game's own single-instance handling after READY and is not part of the launcher's status wait (the old single 30 s cap never waited 74 s). The two phases only bound helper startup and game-process creation.
- The .NET Wine verifier only reuses a Core build output that is not older than the helper sources, and otherwise publishes on the spot, so editing the helper can no longer silently validate a stale binary.
- Helper Wine quirks are now pinned down: the `lpValue` handle variable passed to `UpdateProcThreadAttribute` must stay alive until `CreateProcessW` returns (Wine stores the pointer instead of copying the value; freeing early fails with `ERROR_INVALID_HANDLE (6)`), and NativeAOT cannot cross-compile across OSes, so the helper uses self-contained single-file publishing. See `docs/PITFALLS.md`.

**Fixed — pre-downloaded split volumes from an older launcher build are no longer wasted (2026-10-17)**

- Staged manifests written by an older build lack the split-volume flag, which used to send the apply step back to per-volume extraction (exactly the `End of Central Directory` failure users reported) after tens of GiB had already been downloaded. The installer now recomputes the classification with the same rule the channel uses, restores the volume order and reuses the download; gapped volume sets fail with a clear error before extraction.

**Fixed — stale files from the previous Endfield version are now cleaned up (user report, 2026-10-17)**

- Package updates only extract over the old install and never delete anything, so leftovers accumulate per version (measured: 47.72 GiB of old VFS chunks from 1.5.3 are absent from the 1.6.4 archive). After an update lands, the launcher now applies the **official obsolete-file list**: requesting the update with the local version returns the "local -> latest" patch whose `delete_files.txt` names exactly the files the vendor drops (324 entries measured on 1.6.4). The list lives inside a ~49 GiB patch archive and is read with HTTP Range requests only - tail, central directory, that one entry (4 requests / ~24 KB measured, never the whole archive).
- Cleanup defenses: only files named by the list and absent from this run's extraction are deleted (the official list and the new archive share zero entries on real data, and extracted files win regardless); paths must stay inside the install directory and must not traverse links; the `.yagl` state directory is never touched; emptied directories are pruned; a single failed delete only logs and continues. An unavailable list (unsupported channel, server already moved on, network failure) never fails the update - leftovers simply survive until the next one.
- Also fixed a real-machine transient network failure that silently skipped cleanup: the remote range reader retries 5xx/408/429/connection resets with linear backoff (other 4xx are permanent) and refuses to buffer a whole archive when a server ignores Range.

**Fixed — Endfield package update failed at extraction (user report, 2026-10-17)**

- Updates failed right after downloading with "Failed to extract Beyond_Release_...official.zip.001: End of Central Directory record could not be found": the official `pkg.packs` are **byte volumes of one archive** (59 volumes ~58GiB measured on 1.6.4, `.zip.001...059`), and each volume used to be extracted as if it were a standalone zip - the central directory and EOCD live in the last volume only, so per-volume extraction always fails. `SplitVolumeStream` now presents the volumes as one continuous seekable stream and extracts the archive once (no concatenated copy on disk, so tens of GiB of volumes do not need an equal amount of scratch space), effective on both the fresh-install and predownload/apply chains; already-downloaded volumes are reused (size+MD5) on retry instead of being fetched again.
- Volume sets whose numbering is not contiguous, or that use multiple base names (self-contradictory server data), are now rejected up front with an explanation - users no longer download tens of GiB first only to hit a categoriless EOCD error at extraction time.

**Changed — Endfield delta chain completion (2026-10-18)**

- Predownloads now use the v2 delta payload too: the predownload window stages the 8.72 GiB payload instead of the 58 GiB package, apply reuses it with zero re-download, and a no-longer-applicable staging is cleared before the regular update runs (the predownload target is never recorded as the current latest).
- The predownload delta target is matched by `patch.version` (review fix): during the window the live version is still behind and the patch node describes the upcoming target, so the old guard returned no plan and silently fell back to the 58 GiB package; the manifest version is still checked character-for-character.
- A failed delta predownload now clears only the delta side (payload, `delta.json`, delta marker manifest) and keeps the previously staged full package under `.yagl/predownload/packages`, reused by size+MD5 (review fix): falling back no longer discards tens of GiB already on disk.
- Free-disk-space pre-check before applying (payload + missing new content + one transient file), with required/available figures and a "the payload is reused" hint; shortfalls do not fall back to the full package, which needs even more room.
- The plan's verify-only nodes now get a content MD5 check on top of the size check; equal-sized but corrupted files are no longer waved through (the delta falls back to the full package).
- Large payload entries (1.2 GiB measured in 1.6.4) stream from the archive straight to disk instead of being materialised in memory (~2x peak).
- Patch-tool unavailability (missing tool / failed auto-provisioning / process cannot start) is now distinct from an unusable payload: the downloaded 8.7 GiB payload is kept and the error is retryable, so a fixed tool retries with zero re-download.
- Blind version registration no longer poses as "up to date": the status reads "registered (unverified) - run verify & repair" until the verify & repair action completes (a half-applied install is no longer masked).
- New app-level log file `logs/yagl-yyyyMMdd.log` (14-day retention, write failures swallowed) so update/predownload events and exception tails are on disk instead of being reconstructed from file mtimes.
- The remaining off-grid spacing/padding converged to the 4pt grid (card internals, overlay button rows, toast gap and padding). The launch button is now disabled while a download/update runs (it used to only short-circuit inside the command), the busy progress bar uses on-artwork tokens (the track no longer disappears in the light theme), and content icons in disabled buttons follow the dimmed foreground.

**Fixed — other**

- Third-party license list on the About page corrected: HDiffPatch and FFmpeg.AutoGen marked MIT (verified against the upstream LICENSE / NuGet license file) and Avalonia.Fonts.Inter added (MIT package, Inter typeface OFL-1.1).

---

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
