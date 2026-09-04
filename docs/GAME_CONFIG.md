# games.json 配置参考

YetAnotherGameLauncher 通过一个 JSON 文件描述全部游戏，**代码零硬编码**。
配置文件位置：

- Linux：`~/.config/yagl/games.json`
- Windows：`%APPDATA%\yagl\games.json`
- 环境变量 `YAGL_CONFIG=/path/to/games.json` 可覆盖（测试/多实例）

首次运行无配置时启动器会提示；最快的方式是复制 [`samples/games.json`](../samples/games.json)
到上述位置（已含鸣潮三服与终末地国际服的官方端点）。
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
| `theme` | `"System" \| "Light" \| "Dark"` | `"System"` | 界面主题，System 跟随操作系统 |
| `maxParallelDownloads` | int | `8` | 文件级下载并发（1–64） |
| `language` | string | `"zh-CN"` | 界面语言（预留） |

### games[]（GameDefinition）

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | ✔ | 唯一标识，仅允许字母/数字/`-`/`_`/`.`；写入本地状态文件用于校验 |
| `displayName` | string | ✔ | 界面显示名（列表图标取首字） |
| `channel` | string | ✔ | 渠道实现键：`"kuro"`（鸣潮及库洛系）、`"hypergryph"`（终末地/GRYPHLINE） |
| `icon` | string | | 图标路径（预留，当前 UI 用首字图标） |
| `installDir` | string | ✔ | 安装目录；相对 `settings.installRoot`，也可为绝对路径 |
| `executable` | string | ✔ | 游戏可执行文件，相对 `installDir`（`/` 或 `\` 均可） |
| `launch` | object | | 启动方式，见下 |
| `servers[]` | array | ✔（≥1） | 服务器/渠道入口列表，见下 |

### games[].launch（LaunchOptions）

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `commandTemplate` | string | `"{exe}"` | 启动命令模板；含空格的路径请加引号，如 `wine "{exe}"` |
| `workingDirectory` | string | `"{installDir}"` | 工作目录模板 |
| `environment` | object | `{}` | 附加环境变量（值支持占位符），如 `{"WINEPREFIX": "~/prefix"}` |

可用占位符：

- `{exe}` —— 可执行文件完整路径（`installDir` + `executable`）
- `{installDir}` —— 安装目录绝对路径

### games[].servers[]（GameServer）

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | ✔ | 服务器标识（同游戏内唯一，忽略大小写） |
| `name` | string | ✔ | 界面显示名（如"国服"） |
| `options` | object | | **渠道自定义选项**，由 `channel` 实现解释，见下表 |

### 各渠道的 options

| channel | 键 | 必填 | 说明 |
|---|---|---|---|
| `kuro` | `indexUrl` | ✔ | 库洛 launcher `index.json` 完整地址（每服一个） |
| `hypergryph` | `apiBase` | ✔ | GRYPHLINE 启动器 API 基址（国际服为 `https://launcher.gryphline.com/api`） |

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
        { "id": "cn", "name": "国服", "options": { "indexUrl": "https://prod-cn-alicdn-gamestarter.kurogame.com/launcher/game/G152/10003_.../index.json" } },
        { "id": "global", "name": "国际服", "options": { "indexUrl": "https://prod-alicdn-gamestarter.kurogame.com/launcher/game/G153/50004_.../index.json" } }
      ]
    },
    {
      "id": "arknights-endfield",
      "displayName": "明日方舟：终末地",
      "channel": "hypergryph",
      "installDir": "ArknightsEndfield",
      "executable": "ArknightsEndfield/Binaries/Win64/ArknightsEndfield.exe",
      "servers": [ { "id": "global", "name": "国际服", "options": { "apiBase": "https://launcher.gryphline.com/api" } } ]
    }
  ]
}
```

## 3. Linux：wine / Proton 启动示例

两款游戏均为 Windows 程序，Linux 上把 `launch` 改成包装命令即可：

```jsonc
// wine（独立 prefix 示例）
"launch": {
  "commandTemplate": "wine \"{exe}\"",
  "environment": { "WINEPREFIX": "~/Games/wuwa-prefix" }
}

// Steam Proton（通过 umu-launcher 等包装器）
"launch": {
  "commandTemplate": "umu-run \"{exe}\"",
  "environment": { "GAMEID": "wuwa", "WINEPREFIX": "~/Games/wuwa-umu" }
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

- `settings.installRoot` 不能为空；`maxParallelDownloads ∈ [1, 64]`
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
