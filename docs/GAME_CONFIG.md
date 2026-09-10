# games.json 配置参考

YetAnotherGameLauncher 通过一个 JSON 文件描述全部游戏，**代码零硬编码**。
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
| `language` | string | `"system"` | 界面语言：`"system"` 跟随系统 / `"zh-CN"` / `"en-US"`（也可在设置页切换，即时生效）。字段缺省时应用内默认 `zh-CN`，但首次运行模板写入 `"system"`（跟随系统） |
| `sidebarExpanded` | bool | `true` | 侧栏是否展开（`false` 为图标窄条模式，由界面折叠按钮切换） |
| `appBackgroundImage` | string | | 应用自有背景图（设置/关于页与侧栏底色）：本地文件路径或 http(s) URL；留空使用内置的主题感知渐变。可在设置页"应用背景"卡选择图片或恢复默认。游戏详情页背景不受此项影响 |
| `proxyMode` | `"System" \| "None" \| "Manual"` | `"System"` | 出站网络代理：System 跟随系统代理 / None 直连 / Manual 使用 `proxyAddress` |
| `proxyAddress` | string | | 手动代理地址（如 `http://127.0.0.1:7890`）；`proxyMode` 为 `"Manual"` 时必填且须为可解析的 http(s) URL，其余模式可有可无 |
| `schemaVersion` | int | `0` | 配置结构版本（内部使用）。旧版本配置首次被新版加载时自动迁移：补齐内置模板中同一游戏新增的官方服务器与本地化名称，并写回 `schemaVersion: 3`，仅执行一次 |

### games[]（GameDefinition）

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | ✔ | 唯一标识，仅允许字母/数字/`-`/`_`/`.`；写入本地状态文件用于校验 |
| `displayName` | string | ✔ | 默认显示名（列表图标取首字） |
| `nameLocalized` | object | | 显示名本地化映射，如 `{"zh-CN": "鸣潮", "en-US": "Wuthering Waves"}`；按界面语言取值，缺失回退 `displayName` |
| `channel` | string | ✔ | 渠道实现键：`"kuro"`（鸣潮及库洛系）、`"hypergryph"`（终末地/GRYPHLINE） |
| `icon` | string | | 官方游戏图标：http(s) URL 或本地路径；加载失败回退显示名首字 |
| `installDir` | string | ✔ | 安装目录；相对 `settings.installRoot`，也可为绝对路径 |
| `executable` | string | ✔ | 游戏可执行文件，相对 `installDir`（`/` 或 `\` 均可） |
| `launch` | object | | 启动方式，见下 |
| `servers[]` | array | ✔（≥1） | 服务器/渠道入口列表，见下 |
  服务器在启动器内通过游戏设置页（从详情页齿轮进入）的独立「服务器」卡切换，
  与位置卡分离（先选区、再选目录）；切换即时刷新版本/安装状态。

### games[].launch（LaunchOptions）

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `commandTemplate` | string | `"{exe}"` | 启动命令模板；含空格的路径请加引号，如 `wine "{exe}"` |
| `workingDirectory` | string | `"{installDir}"` | 工作目录模板 |
| `environment` | object | `{}` | 附加环境变量（值支持占位符），如 `{"WINEPREFIX": "~/prefix"}` |

可用占位符：

- `{exe}` —— 可执行文件完整路径（`installDir` + `executable`）
- `{installDir}` —— 安装目录绝对路径

> [!NOTE]
> Linux 首运生成默认配置时，裸 `{exe}` 模板（无法运行 Windows 客户端）会被自动升级为
> 社区推荐链 + 兼容环境变量并写盘：**umu-launcher**（`umu-run {exe}` + GAMEID/UMU_ID/WINEPREFIX，
> 未装时先给模板、可在启动设置卡一键安装）→ **Proton**（STEAM_COMPAT 环境）→ **系统 wine**（WINEPREFIX）。
> （`MainWindowViewModel.ApplyLinuxFirstRunLaunchDefaultsAsync`，推荐逻辑单一来源
> `CompatTools.BuildRecommendedLaunch`。）仅在首运生成那一刻执行一次，此后配置以用户修改为准。
>
> Wine prefix 由启动器统一放在 `{数据目录}/yagl/prefixes/<游戏id>`
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
| `hypergryph` | `apiBase` | ✔ | 启动器 API 基址：国际服 `https://launcher.gryphline.com/api`，国服/B服 `https://launcher.hypergryph.com/api` |
| `hypergryph` | `appcode` | | 游戏 appcode；缺省为国际服 `YDUTE5gscDZ229CW`，国服/B服为 `6LL0KJuqHBVz33WK` |
| `hypergryph` | `channel` | | 渠道号；缺省 `6`（国际服），国服 `1`，B服 `2` |
| `hypergryph` | `subChannel` | | 子渠道号；缺省 `9999`（国际服实测值），国服 `1`，B服 `2` |

> 终末地三个官方服务器（国际服 / 国服 / B服）的完整参数已内置于
> [`samples/games.json`](../samples/games.json)，直接使用即可；上述键仅供未来新增渠道时覆盖。

### 详情页背景与名称本地化（代码内置，不写入配置）

- **详情页背景**：配置文件不携带背景地址。每次启动按界面语言选择渠道（中文 → 国服端点，其余 → 国际服端点）向官方接口确认当期背景，地址变化时自动下载到应用数据目录缓存（配置目录下 `backdrops/<gameId>/`，Windows 即 `%APPDATA%\yagl\backdrops\`），离线时回退上次缓存：
  - 终末地：官方启动器 `get_main_bg_image` 接口（当期版本主视觉，端点参数取自该游戏 `servers[].options`）。
  - 鸣潮：直连官方启动器运营配置 `switch.json`（背景视频 + 首帧图，随官方投放即时更新）；不可用时回退本机库洛启动器 WebView 缓存，再回退本地帧序列（`kr_game_cache\animate_bg`）。
  - 均不可用时回退主题渐变背景。
- **游戏名**：`nameLocalized` 按界面语言显示（样例模板已含鸣潮/终末地中英文名，旧配置自动迁移补齐）；服务器名等其余配置数据按配置文件原样显示。

## 2. 完整示例

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
      "launch": { "commandTemplate": "{exe}" },
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
      "executable": "ArknightsEndfield/Binaries/Win64/ArknightsEndfield.exe",
      "servers": [ { "id": "global", "name": "Global", "options": { "apiBase": "https://launcher.gryphline.com/api" } } ]
    }
  ]
}
```

## 3. Linux：umu / wine / Proton 启动示例

两款游戏均为 Windows 程序，Linux 上把 `launch` 改成包装命令即可。
**推荐直接用启动器的 umu-launcher 引导安装**（启动设置卡或启动失败提示内一键安装），
装完由启动器自动生成以下配置，无需手写：

```jsonc
// umu-launcher（推荐：自动管理 Steam Runtime 容器与 Proton；prefix 由启动器统一管理）
"launch": {
  "commandTemplate": "umu-run \"{exe}\"",
  "environment": {
    "GAMEID": "umu-wuthering-waves",   // 能命中 umu 数据库时自动套用社区修复
    "UMU_ID": "umu-wuthering-waves",
    "WINEPREFIX": "~/.local/share/yagl/prefixes/wuthering-waves"
  }
}

// 系统 wine
"launch": {
  "commandTemplate": "wine \"{exe}\"",
  "environment": { "WINEPREFIX": "~/.local/share/yagl/prefixes/wuthering-waves" }
}

// Proton 直启（自备 Steam + compatibilitytools.d；或 Steam 商店版本走协议启动）
"launch": {
  "commandTemplate": "\"~/.steam/steam/compatibilitytools.d/GE-Proton10-9/proton\" run {exe}",
  "environment": {
    "STEAM_COMPAT_DATA_PATH": "~/.local/share/yagl/prefixes/wuthering-waves",
    "STEAM_COMPAT_CLIENT_INSTALL_PATH": "~/.steam/steam",
    "SteamOS": "1"                      // 鸣潮过 ACE 反作弊需伪装 SteamOS；NVIDIA 卡再加 PROTON_ENABLE_NVAPI=1
  }
}

// Steam 商店版本（用 steam 协议启动，忽略 exe）
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

配置加载失败时会**一次性列出全部错误**（中英文混合提示含字段定位）：

- `settings.installRoot` 不能为空；`proxyMode` 为 `"Manual"` 时 `proxyAddress` 必须为可解析的 http(s) URL
- `games[].id` 非空、无非法字符、全局唯一（忽略大小写）
- `displayName` / `channel` / `installDir` / `executable` / `launch.commandTemplate` 非空
- `servers` 至少 1 个；服务器 `id` 唯一且非空、`name` 非空
- JSON 语法错误 / 未知枚举值 → 归一为校验异常提示

## 6. 运行时产生的状态文件

| 路径（相对安装目录） | 内容 |
|---|---|
| `.yagl/state.json` | 本地版本状态：`gameId`、`serverId`、`version`（切换游戏/服务器错配时视为未安装） |
| `.yagl/predownload/` | 预下载暂存：`manifest.json`（暂存清单）+ `patches/`、`files/` 或 `packages/` |
| `.yagl/packages` | 包式安装的临时压缩包（解压后清理） |
| `*.yagl-bak` | 差分替换的备份文件（成功即删，仅失败恢复时短暂存在） |

清理游离文件时，`Saved/` 存档目录、`.yagl/`、`launcherDownloadConfig.json`（官方启动器兼容）永远保留。
