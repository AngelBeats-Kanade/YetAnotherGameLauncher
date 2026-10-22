# games.json 配置参考

YetAnotherGameLauncher 通过一个 JSON 文件描述全部游戏，**代码零硬编码**。
本文件是配置字段的权威参考；**绝大多数字段无需手改**——全局 `settings` 与每个游戏的
常用配置（安装目录/启动方式/Proton 发行版/命令模板/自定义启动选项等）都能在
启动器界面完成（设置页与游戏设置页，见 README「界面操作指南」，改动写回本文件；
服务器下拉仅当前会话生效、不落盘），手动编辑 JSON 属于高级用法（添加新游戏、微调渠道参数等）。
配置文件位置：

- Linux：`~/.config/yagl/games.json`
- Windows：`%APPDATA%\yagl\games.json`
- 环境变量 `YAGL_CONFIG=/path/to/games.json` 可覆盖（测试/多实例）

首次运行无配置时启动器会自动生成默认配置（`GameCatalogService.CreateDefaultFileAsync`
写入内嵌的 [`samples/games.json`](../samples/games.json) 模板；Linux 上会顺带把默认
`{exe}` 模板升级为社区推荐链（umu → Proton → wine），见下文 [launch 小节](#gameslaunchlaunchoptions)），
也可手动复制该样例到上述位置；
鸣潮与终末地均已内置国际服/国服/B 服三服的官方端点。
文件支持注释（`//`）与尾逗号；保存后重启启动器生效。

## 1. 顶层结构

```jsonc
{
  "settings": { ... },   // 全局设置（必填 installRoot）
  "games":    [ ... ]    // 游戏列表（可以为空）
}
```

### settings

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `installRoot` | string | 必填 | 安装根目录，支持 `~` 展开；游戏 `installDir` 相对它解析 |
| `theme` | `"System" \| "Light" \| "Dark"` | `"System"` | 界面主题，System 跟随操作系统（也可在设置页切换） |
| `downloadSpeedLimitBytes` | long | `0` | 下载限速（字节/秒）；`0` = 不限速 |
| `language` | string | `"zh-CN"` | 界面语言：`"system"` 跟随系统 / `"zh-CN"` / `"en-US"`（也可在设置页切换，即时生效）。模型默认 `zh-CN`；首次运行写入的样例模板是 `"system"`（跟随系统） |
| `sidebarExpanded` | bool | `true` | 侧栏是否展开（`false` 为图标窄条模式，由界面折叠按钮切换） |
| `windowWidth` | int? | | 主窗口宽度持久化（DIP），关闭窗口时写回；缺省用内置默认。内部字段，设置页不展示 |
| `windowHeight` | int? | | 主窗口高度持久化（DIP），关闭窗口时写回；缺省用内置默认。内部字段，设置页不展示 |
| `windowMaximized` | bool | `false` | 关闭时是否处于最大化（按视觉最大化判定，详见 ARCHITECTURE.md）。内部字段，设置页不展示 |
| `appBackgroundImage` | string | | 应用自有背景图（设置/关于页与侧栏底色）：本地文件路径或 http(s) URL；留空使用内置的主题感知渐变。可在设置页"应用背景"卡选择图片或恢复默认。游戏详情页背景不受此项影响 |
| `proxyMode` | `"System" \| "None" \| "Manual"` | `"System"` | 出站网络代理：System 跟随系统代理 / None 直连 / Manual 使用 `proxyAddress` |
| `proxyAddress` | string | | 手动代理地址（如 `http://127.0.0.1:7890`）；`proxyMode` 为 `"Manual"` 时必填且须为可解析的 http(s) URL，其余模式可有可无 |
| `closeAction` | `"Exit" \| "HideToTray"` | `"Exit"` | 标题栏关闭按钮行为：`Exit` 退出应用 / `HideToTray` 只隐藏窗口并驻留系统托盘（托盘图标唤回，下载与依赖安装继续）。可在设置页切换 |
| `schemaVersion` | int | `0` | 配置结构版本（内部使用）。旧版本配置首次被新版加载时自动迁移并写回版本号，仅执行一次：`3` 补齐内置模板中同一游戏新增的官方服务器与缺失图标（本地化名称仅在映射整体为空时补齐）；`4` 在 Linux 上把裸 `{exe}` 启动模板升级为推荐链（其他平台只推进版本号）；`5` 在 Linux 上把存量 `umu-run {exe}` 模板（历史自动生成形态）升级为推荐链；`6` 把内置样例中终末地条目开启的 `launch.decoyParent`（诱饵父进程桥）旗标并入存量配置；`7` 把内置样例中鸣潮官服的 `servers[].options.resourcePackIndexUrl`（档位资源包索引）并入存量配置（已有该键时不覆盖） |

### games[]（GameDefinition）

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | ✔ | 唯一标识，仅允许字母/数字/`-`/`_`/`.`；`.` 与 `..` 是路径保留段，结尾不能是 `.`（Windows 会规范化尾点，`game.` 与 `game` 同目录）；写入本地状态文件用于校验 |
| `displayName` | string | ✔ | 默认显示名（列表图标取首字） |
| `nameLocalized` | object | | 显示名本地化映射，如 `{"zh-CN": "鸣潮", "en-US": "Wuthering Waves"}`；按界面语言取值，精确项缺失时按语言前缀回退（如 `zh-TW` → `zh-CN`），仍无则回退 `displayName` |
| `channel` | string | ✔ | 渠道实现键：`"kuro"`（鸣潮及库洛系）、`"hypergryph"`（终末地/GRYPHLINE） |
| `icon` | string | | 官方游戏图标：http(s) URL、本地路径或随包内置资源（`avares://…`，内置样例即用）；加载失败回退显示名首字 |
| `installDir` | string | ✔ | 安装目录；相对 `settings.installRoot`，也可为绝对路径 |
| `executable` | string | ✔ | 游戏可执行文件，相对 `installDir`（`/` 或 `\` 均可）。按实际游戏布局填（鸣潮 `Client/Binaries/Win64/Client-Win64-Shipping.exe`、终末地 `Endfield.exe`——2026-09 实测：官方安装/整包解压后主程序在安装目录根） |
| `launch` | object | | 启动方式，见下 |
| `servers[]` | array | ✔（≥1） | 服务器/渠道入口列表，见下 |
  服务器在启动器内通过游戏设置页（从详情页齿轮进入）的独立「服务器」卡切换，
  与位置卡分离（先选区、再选目录）；切换即时刷新版本/安装状态。

### games[].launch（LaunchOptions）

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `commandTemplate` | string | `"{exe}"` | 启动命令模板；含空格的路径请加引号，如 `wine "{exe}"` |
| `workingDirectory` | string | `"{installDir}"` | 工作目录模板（仅模板直启链读取；原生 umu 链固定以 `installDir` 为工作目录） |
| `environment` | object | `{}` | 附加环境变量（值支持 `{exe}`/`{installDir}` 占位符，**不做 `~` 展开**——路径请写绝对路径），如 `{"WINEPREFIX": "/home/you/prefix"}`。设置页「自定义启动选项」框以 Steam 启动选项风格文本编辑这里**用户自定义**的键：占位符 `%command%` 之前的区域，空白分隔多条 `KEY=VALUE`、值含空格用引号包裹（如 `DXVK_NVAPI_DRS_SETTINGS="…" PROTON_LOG=1`），末尾的 `%command%` 由启动器自动补全、无需手写；JSON 仍是 object 字典、键名不变，旧配置零迁移。推荐链生成的键（GAMEID/UMU_ID/WINEPREFIX/PROTONPATH/STEAM_COMPAT_* 等）由启动器托管、不在框内显示，保存时用户键与托管键合并、**同名时用户键优先** |
| `arguments` | string[] | | 自定义启动选项中显式 `%command%` 之后的游戏命令行参数（已去引号的 token 列表），启动时按序追加在游戏可执行文件之后、`-krqlv` 档位参数之前，两条启动链均生效；token 支持 `{exe}`/`{installDir}` 占位符。留空/缺省 = 不追加；框内未写 `%command%` 时等价于自动补在末尾（纯环境变量语义），仅在需要给游戏追加参数时才需显式写出，如 `%command% -dx11` |
| `umuId` | string | | umu 启动用的 UMU_ID 覆盖（形如 `umu-3513350`，对齐 [umu 数据库](https://github.com/Open-Wine-Components/umu-database)规范 ID）；留空按 `umu-{游戏id}` 生成。仅影响 GAMEID/UMU_ID，prefix 路径不变。显式设置时为 UMU_ID/GAMEID 的权威值（`environment` 里的同名残留不会反超） |
| `useWayland` | bool | `false` | 注入 `PROTON_USE_WAYLAND=1` 启用 Proton 原生 Wayland 驱动（绕过 XWayland）。仅 Linux Proton 启动链；DW/GE/UMU-Proton 均识别（DW 另接受 PROTON_ENABLE_WAYLAND 别名）。设置页「功能开关 → 使用 Wayland」 |
| `upgradeDlss` | bool | `false` | 注入 `PROTON_DLSS_UPGRADE=1`（连带 `PROTON_ENABLE_NVAPI=1`）：启动时由 protonfixes 把游戏内 DLSS 换成 Proton 内置新版。仅 NVIDIA 显卡生效。设置页「功能开关 → 升级 DLSS 模型」 |
| `enableProtonLog` | bool | `false` | 注入 `PROTON_LOG=1` + `PROTON_LOG_DIR={启动日志目录}`（与 `launch-*.log` 同源），记录 Proton 运行日志；功能开关打开时覆盖用户手写的同名 `PROTON_LOG_DIR`。设置页「功能开关 → 打印 Proton 日志」 |
| `resourceQualityTier` | string | | 鸣潮资源包档位：`hd` / `sd` / `uhd` 之一（白名单外/大写拒收）。**两个作用**：①启动时以 `-krqlv=<tier>` 命令行参数传给游戏（官方启动器同款参数，两条启动链均生效）；②决定下载/补包走哪一档资源（2026-10-19 起：同步清单按该档裁剪未选中档位目录 + 只补目标档位包）。留空 = 跟随游戏内设置（生效档 = `hd`，即官方默认 bundle = common + HD），且不追加参数。设置页下拉仅鸣潮渠道显示；启动链不按渠道过滤——手改 JSON 给非鸣潮游戏填了该值仍会传参，非鸣潮请保持留空。按档位下载需服务器带 `resourcePackIndexUrl`（见下表）；没有该地址的服务器档位只作启动参数（设置页给出提示）。档位资源"追加式共存"：切换档位不删旧档内容，清理只经设置页「清理其他档位」显式动作——详见 §1「档位资源包」节 |
| `decoyParent` | bool | `false` | 诱饵父进程桥（终末地 1.6.3+ 启动校验的修复，2026-10-18；Windows 直启 2026-10-20 接入）：Linux 原生 umu 链与 Windows 直接运行（`{exe}`）均生效。开启后在启动前把游戏目录里鹰角签名的可执行文件（`PlatformProcess.exe` → `UnityCrashHandler64.exe` → `Endfield.exe` 首个存在者）复制为诱饵 `Games.exe`（内容与候选一致时复用现有文件——Windows 上已挂起的诱饵会锁住镜像文件，避免第二次启动被误报为目录不可写；工作目录在启动器数据目录下的 `bridge/<游戏id>/`——`{数据目录}/bridge/<游戏id>`，Linux `~/.local/share/yagl/bridge/<id>`、Windows `%LOCALAPPDATA%\yagl\bridge\<id>`；游戏安装目录只读，旧版 `<安装目录>/.yagl/bridge` best-effort 清理），并以挂起状态作为游戏进程的父进程（不开启时游戏会弹「请通过官方启动器打开游戏」并以退出码 5 结束）。失败处理按平台/阶段：Linux 候选缺失/准备失败静默回退直启；Windows 必须由 helper 拉起游戏 exe——候选缺失、桥目录不可写或 `commandTemplate` 未直接指向游戏 exe 都会抛可操作错误；准备成功后 helper 在游戏进程创建前失败（状态文件 `ERROR:<码>:<消息>`）在两个平台都抛错，`READY` 之后游戏自身退出码（如校验拒绝的 5）不再算桥失败（静默直启必被游戏拒绝）。内置样例的终末地条目已开启；存量配置由 `schemaVersion 6` 一次性迁移补上；机制、三层判据与真机实测见 [ARCHITECTURE.md §3.1](ARCHITECTURE.md) |

可用占位符：

- `{exe}` —— 可执行文件完整路径（`installDir` + `executable`）
- `{installDir}` —— 安装目录绝对路径

> [!NOTE]
> Linux 首运生成默认配置时，裸 `{exe}` 模板（无法运行 Windows 客户端）会被自动升级为
> 社区推荐链 + 兼容环境变量并写盘：**原生 umu**（`native-umu {exe}` + GAMEID/UMU_ID/WINEPREFIX/
> STEAM_COMPAT_DATA_PATH/PROTONPATH，内置 C# 启动链，启动时自动准备 Proton 与 Steam Runtime）→
> **Proton** → **系统 wine**。
> （`MainWindowViewModel.ApplyLinuxFirstRunLaunchDefaultsAsync`，推荐逻辑单一来源
> `CompatTools.BuildRecommendedLaunch`。）仅在首运生成那一刻执行一次，此后配置以用户修改为准。
>
> 设置页启动方式二选一：**umu 启动**（默认，旁边可选 Proton 发行版 DW/GE/UMU-Proton，
> 选择写入 `environment.PROTONPATH` 代号并即时保存——只下载所选发行版，空配置兜底 DW-Proton；
> 下载资产按主机架构匹配（x86_64/aarch64），杜绝装错架构）与**直接运行**；
> 发行版本体用"检查更新"按钮显式检测/更新（更新后自动清理同发行版旧版本目录）；
> 旧版 wine/Proton 模板仍可运行，进设置页仅作 umu 显示映射，主动切换并保存后才会改写。
>
> Wine prefix 由启动器统一放在 `{数据目录}/prefixes/<游戏id>`
> （Linux `~/.local/share/yagl/prefixes/`），不写入游戏安装目录——
> 安装同步的清单外清理不会误删 prefix；需要独立 prefix 时用 `WINEPREFIX`/`STEAM_COMPAT_DATA_PATH` 覆盖。

### games[].servers[]（GameServer）

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | ✔ | 服务器标识（同游戏内唯一，忽略大小写） |
| `name` | string | ✔ | 界面显示名（任意语言，原样显示；内置样例用英文 Global/CN/Bilibili） |
| `options` | object | | **渠道自定义选项**，由 `channel` 实现解释，见下表 |

### 各渠道的 options

| channel | 键 | 必填 | 说明 |
|---|---|---|---|
| `kuro` | `indexUrl` | ✔ | 库洛 launcher `index.json` 完整地址（每服一个） |
| `kuro` | `resourcePackIndexUrl` | | 官方**档位索引**完整地址（`resourcePacks` 节点，2026-10-16 端点实测；与 `indexUrl` 同源、hash 体系不同、不含版本号）。提供后该服务器支持「按 `launch.resourceQualityTier` 下载/补包/清理档位资源」；缺省 = 档位只作启动参数（设置页给出提示、清理入口隐藏）。内置样例只为官服（CN）提供——国际服/B 服的档位索引路径实测 404（2026-10-19 端点实测） |
| `hypergryph` | `apiBase` | ✔ | 启动器 API 基址：国际服 `https://launcher.gryphline.com/api`，国服/B服 `https://launcher.hypergryph.com/api` |
| `hypergryph` | `appcode` | | 游戏 appcode；代码缺省 = 国际服值 `YDUTE5gscDZ229CW`（国服/B服值 `6LL0KJuqHBVz33WK` 须显式提供） |
| `hypergryph` | `channel` | | 渠道号；代码缺省 `6`（国际服），国服 `1`、B服 `2` 须显式提供 |
| `hypergryph` | `subChannel` | | 子渠道号；代码缺省 `9999`（国际服实测值），国服 `1`、B服 `2` 须显式提供 |

> 终末地三个官方服务器（国际服 / 国服 / B服）均已内置于
> [`samples/games.json`](../samples/games.json)：国际服条目依赖上述缺省值，国服/B服条目显式给出全部键，直接使用即可；上述键仅供未来新增渠道时覆盖。

### 档位资源包（鸣潮，2026-10-19）

游戏本体按"common + 一个档位包"组成（官方 bundle：SD = common + SD 21.97 GB / HD = common + HD 45.71 GB /
UHD = common + UHD 66.04 GB，2026-10-16 端点实测）。`launch.resourceQualityTier` 为 `sd`/`uhd` 时：

- **全量同步/校验修复**按该档构建清单：裁掉其它档位目录（`Client/Content/{SD,HD,UHD}/`）、补上目标档位包；
  与档位无关的 common 内容不受影响。
- **增量更新/预下载**只取基础差量（档位包跨版本不变，官方实测 hd/sd/uhd 的差量清单为空）；
  档位包自身的补齐由更新/应用后的全量修复轮承担。
- **追加式共存**：切换档位不删除旧档内容（未选中档位目录列入"保留清单"，同步收尾清理与增量废弃清单
  都不碰它们）；释放空间只经设置页「清理其他档位」（先预览释放量与目标档位、再确认删除；只删各档位清单
  点名且解析后位于该档位目录下的文件与同名下载半成品 `.temp`）。确认文案在"选中档位未完整落盘"时
  加警示（含下到一半的情形），选中档位清单取不到时如实说明而未作断言。
- **档位状态**记在 `.yagl/state.json` 的 `resourceTier` 字段（安装/更新/校验成功后由启动器写入；
  旧状态文件没有该字段 = 视为"未同步"）。已安装且本地档位与所选档位不一致时，主按钮变为
  「下载资源包」（优先于"立即更新"：点它做的是更新 + 补包一体），状态行给出提示。
- **档位索引拉取失败/目标档位缺失时拒收**（不返回"缺目标档位包"的清单——那会让收尾清理把目标档位
  目录当游离文件删掉）；档位索引不可用的服务器（国际服/B 服）档位只作启动参数。
- 「清理其他档位」在切换档位后即可执行（不要求先下载新档）：若选中档位尚未落盘，确认文案会附加
  "补齐前游戏可能无法正常运行"的警示。
- 存量配置由 `schemaVersion 7` 一次性迁移补上样例里的 `resourcePackIndexUrl`（迁移清单见 §1 `settings.schemaVersion`）。

### 详情页背景与名称本地化（代码内置，不写入配置）

- **详情页背景**：配置文件不携带背景地址。背景缓存元数据记录抓取时的区域与游戏版本，两者均未变化时启动直接用缓存（零网络）；每服务器每启动至多一次版本/预载检测，**游戏版本更新后**的首次解析才按界面语言选择渠道（中文 → 国服端点，其余 → 国际服端点）向官方接口确认当期背景，地址变化时自动下载到应用数据目录缓存（数据目录下 `backdrops/<gameId>/`，Linux 即 `~/.local/share/yagl/backdrops/`、Windows 即 `%LOCALAPPDATA%\yagl\backdrops\`——2026-09-22 起可重建缓存统一归数据目录，见 §6），离线时回退上次缓存：
  - 终末地：官方启动器 `get_main_bg_image` 接口（当期版本主视觉，端点参数取自该游戏 `servers[].options`）。
  - 鸣潮：直连官方启动器运营配置两跳取当期投放（launcher-config 的 `functionCode.background` 哈希 → 背景内容 JSON，背景视频 + 首帧图，随官方投放即时更新；2026-09 起官方弃用固定 `switch.json` 改哈希寻址）。视频不可用时首帧图即一级回退，与终末地同款。
  - 均不可用时回退主题渐变背景。
- **游戏名**：`nameLocalized` 按界面语言显示（样例模板已含鸣潮/终末地中英文名，旧配置自动迁移补齐）；服务器名等其余配置数据按配置文件原样显示。

## 2. 示例（节选）

见 [`samples/games.json`](../samples/games.json)，节选：

```jsonc
{
  "settings": { "installRoot": "~/Games", "theme": "System" },
  "games": [
    {
      "id": "wuthering-waves",
      "displayName": "鸣潮",
      "channel": "kuro",
      "installDir": "WutheringWaves",
      "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
      "launch": { "commandTemplate": "{exe}", "umuId": "umu-3513350" },
      "servers": [
        { "id": "cn", "name": "CN", "options": { "indexUrl": "https://prod-cn-alicdn-gamestarter.kurogame.com/launcher/game/G152/10003_.../index.json" } },
        { "id": "global", "name": "Global", "options": { "indexUrl": "https://prod-alicdn-gamestarter.kurogame.com/launcher/game/G153/50004_.../index.json" } }
      ]
    },
    {
      "id": "arknights-endfield",
      "displayName": "明日方舟：终末地",
      "channel": "hypergryph",
      "installDir": "ArknightsEndfield",
      "executable": "Endfield.exe",
      "servers": [ { "id": "cn", "name": "CN", "options": { "apiBase": "https://launcher.hypergryph.com/api", "appcode": "6LL0KJuqHBVz33WK", "channel": "1", "subChannel": "1" } } ]
    }
  ]
}
```

## 3. Linux：umu / wine / Proton 启动示例

两款游戏均为 Windows 程序，Linux 上推荐**启动设置卡的 umu 启动**（内置 C# 链，自动管理
Steam Runtime 容器与 Proton，发行版三选一），由启动器自动生成以下配置，无需手写：

```jsonc
// umu 启动（默认推荐；umuId 对齐 umu 数据库规范 ID，命中时外部工具链自动套用社区修复）
"launch": {
  "commandTemplate": "native-umu \"{exe}\"",
  "umuId": "umu-3513350",               // 鸣潮 = umu-3513350（Steam AppId）；终末地 = umu-endfield
  "environment": {
    "GAMEID": "umu-3513350",
    "UMU_ID": "umu-3513350",
    "WINEPREFIX": "/home/you/.local/share/yagl/prefixes/wuthering-waves",
    "STEAM_COMPAT_DATA_PATH": "/home/you/.local/share/yagl/prefixes/wuthering-waves",
    "PROTONPATH": "DW-Proton",           // 发行版代号：DW-Proton / GE-Proton / UMU-Proton（缺失时按代号下载本机架构 latest）
    "SteamOS": "1"                       // 鸣潮过 ACE 反作弊需伪装 SteamOS；NVIDIA 卡再加 PROTON_ENABLE_NVAPI=1
  }
}

// 功能开关（设置页「功能开关」区，2026-10-03 前叫「启动选项」；全为 bool、缺省 false，映射见 games[].launch 字段表）
"launch": {
  "commandTemplate": "native-umu \"{exe}\"",
  "useWayland": true,                    // 注入 PROTON_USE_WAYLAND=1（原生 Wayland 驱动）
  "upgradeDlss": true,                   // 注入 PROTON_DLSS_UPGRADE=1 + PROTON_ENABLE_NVAPI=1（NVIDIA）
  "enableProtonLog": false               // 注入 PROTON_LOG=1 + PROTON_LOG_DIR=启动日志目录
}

// 手写 wine / Proton 直启仍受支持（设置页不再提供入口，模板照常执行）
"launch": {
  "commandTemplate": "wine \"{exe}\"",
  "environment": { "WINEPREFIX": "/home/you/.local/share/yagl/prefixes/wuthering-waves" }
}

// Steam 商店版本（用 steam 协议启动；exe 不进命令行，但仍须指向存在的文件——启动预检要求）
"launch": { "commandTemplate": "steam -applaunch 2208930" }
```

> 原生 Linux 游戏同样适用：`commandTemplate: "{exe}"` 且 `executable` 指向 Linux 可执行文件。

## 4. 添加新游戏教程

**场景 A：另一款库洛系游戏（如《战双帕弥什》PC 端）** —— 复用 `kuro` 渠道：

1. 找到该游戏官方启动器的 `index.json` 地址（每服一个）。
2. 在 `games` 数组新增条目：`channel: "kuro"`，`servers[].options.indexUrl` 填端点；
   `executable` 按该游戏目录结构填写（鸣潮是 `Client/Binaries/Win64/Client-Win64-Shipping.exe`）。
3. 重启启动器 → 列表出现新游戏 → "安装游戏" 即下载全量。

**场景 B：任意"官方启动器可下载"的 Windows 游戏** —— 需要新渠道实现（改代码）：
按 [DEVELOPMENT.md §5.1](DEVELOPMENT.md) 实现 `IGameChannelApi`；之后同样只是配置文件里的一行 `channel`。

**场景 C：手动管理的本地游戏（无需下载/更新）** —— 渠道层暂无"本地仅启动"实现；
如需可参考 DEVELOPMENT.md 添加一个返回空清单、只提供版本的 `local` 渠道（欢迎贡献）。

## 5. 校验规则速查

配置加载失败时会**一次性列出全部错误**（英文错误串，含 `games[i]` 字段路径定位）：

- `settings.installRoot` 不能为空；`proxyMode` 为 `"Manual"` 时 `proxyAddress` 必须为可解析的 http(s) URL
- `games[].id` 非空、无非法字符、非 `.`/`..`、不以 `.` 结尾、全局唯一（忽略大小写）
- `displayName` / `channel` / `installDir` / `executable` / `launch.commandTemplate` 非空
- `launch.umuId` 非空时须形如 `umu-<slug>`（后缀仅字母/数字/`-`/`_`）
- `launch.resourceQualityTier` 非空时须为 `hd` / `sd` / `uhd` 之一
- `servers` 至少 1 个；服务器 `id` 唯一且非空、`name` 非空
- JSON 语法错误 / 未知枚举值 → 归一为校验异常提示
- 容器/元素显式 `null`（`settings`、`launch`、`games`/`servers` 容器、`games[]` 元素、`servers[]` 元素、`nameLocalized`）归一为校验错误（不崩溃）；`launch.environment` 与 `servers[].options` 为 `null` 时按空对象处理、不报错

## 6. 运行时产生的状态文件

| 路径（相对安装目录） | 内容 |
|---|---|
| `.yagl/state.json` | 本地版本状态：`gameId`、`serverId`、`version`、`verified`、`resourceTier`（切换游戏/服务器错配时视为未安装）。`verified=false` = 仅由包式渠道"检测到现有文件 → 登记为渠道最新版"的零校验流程写入（未核对任何内容；UI 明示未校验并指向校验修复，旧文件缺该字段时缺省 true）。`resourceTier` = 本地内容对应的档位（`hd`/`sd`/`uhd`，2026-10-19 起由安装/更新/校验修复成功时写入；支持档位下载的渠道写实际落盘档位，鸣潮未配置档位索引的服务器归 `hd`，包式渠道（终末地）不写该字段（`null`）；旧文件缺该字段 = 视为未同步，UI 仅在支持档位下载的服务器提示补包） |
| `.yagl/download-session.json` | 未完成的下载标记（2026-10-19）：操作开始时写（含操作种类与游戏/服务器归属）、<b>成功收尾时清</b>，取消/失败/进程中断都留存——重开应用据此把主按钮显示为「继续下载」，并按种类派回被暂停的动作（预下载 → 预下载链；内容已下完/应用中被停 → 应用链续跑）从已下载处续传。只承载"有没有未完成的操作"，进度以磁盘为准（完成文件跳过、`.temp` 走 HTTP Range、暂存按 size/MD5 复用，均见 §6 同族的暂存目录）；标记带归属，同一安装目录被多服务器复用时只认写入本服务器的那条。**不可继续的预下载标记就地清除**：预下载窗口已关闭或本地版本已覆盖目标时，那条 `predownload` 标记无从继续（留着就是假承诺），Core 在报可行动错误/幂等短路的同时清掉它（其它种类的标记不动）；同一安装目录里未完成的更新标记不受影响 |
| `.yagl/predownload/` | 预下载暂存：`manifest.json`（暂存清单，`stagedAsDeltaPackage=true` 表示增量预下载）+ `patches/`、`files/` 或 `packages/`；增量预下载另有 `delta.json`（v2 应用计划，与 `.yagl/delta` 的载荷配套）。**清理时机 = 各链应用的提交点（状态落盘之后）**：载荷与暂存清单在提交前一律保留（2026-10-19 二审 N2），续跑因此走"应用链续跑"零下载重解压/重应用，而不是重下整包差量 |
| `.yagl/packages` | 包式**整包安装（直接安装/更新、整包回退）的暂存目录**：文件名 + size/MD5 完整时可复用（中断重跑零下载），解压成功后清理；增量更新成功后也会清理它回收空间（实测可达数十 GiB）。预下载内容在 `.yagl/predownload/`，不受影响 |
| `.yagl/delta` | v2 增量载荷分卷暂存（9 卷 ≈8.72 GiB）：**清理时机 = 提交点（状态落盘之后）**，失败/取消保留供复用——应用器不自删载荷（2026-10-19 三审）；预下载链同样落这里（应用由暂存清单的 `stagedAsDeltaPackage` 标记识别）。应用按节点目标 md5 的"已到位跳过"幂等：应用期暂停/掉线后续跑零下载收敛（差分节点以基 md5 为判据，内容已应用再走一遍会被判"不适用"而错误回退整包） |
| `*.yagl-bak` | 差分替换的备份文件（成功即删，仅失败恢复时短暂存在） |
| `*.yagl-delta` / `*.yagl-new` | 增量载荷逐文件合成的临时产物（核对通过即替换目标，随后删除） |

安装目录之外，启动器在**数据目录**（Linux `~/.local/share/yagl`，Windows `%LOCALAPPDATA%\yagl`）
存放可重建缓存与工具数据（2026-09-22 起 ffmpeg/背景/图标/唤取缓存从配置目录迁入，配置目录只留
`games.json` 等不可清理物；旧位置的 `ffmpeg/`、`backdrops/`、`image-cache/`、`gacha/` 成遗留、
可手动删除）：
`ffmpeg/<rid>/`（FFmpeg 原生库，缺失自动重下）、`backdrops/<游戏id>/`（背景视频/首帧图缓存）、
`image-cache/`（http 图标缓存）、`gacha/`（鸣潮唤取记录合并缓存）、`prefixes/<游戏id>/`（Wine prefix，见上文；
其根下 `.yagl-deps/<依赖id>.ok` 为依赖安装完成标记，内容 `依赖id@版本`，随 prefix 删除自动重置；
手改配置自定义 `WINEPREFIX`/`STEAM_COMPAT_DATA_PATH` 时，标记落在对应 prefix 目录内）。
依赖下载缓存另在**系统缓存目录**（Linux `~/.cache/yagl/deps`，Windows `%LOCALAPPDATA%\cache\yagl\deps`）：
压缩包全局共享（多 prefix 复用，size/MD5 双校验），`staging/<依赖id>/` 为解压暂存（用后即清）。
依赖安装管线见 docs/ARCHITECTURE.md §3.9。

清理游离文件时，`Saved/` 存档目录、`.yagl/`、`launcherDownloadConfig.json` 与 `compatdata/`（旧版推荐配置的 Proton prefix 位置，内有注册表/着色器缓存/用户数据）永远保留。清理枚举不穿过目录符号链接/junction（链接目标在安装树之外也绝不会被删），无权限读取的子目录会被静默跳过。
