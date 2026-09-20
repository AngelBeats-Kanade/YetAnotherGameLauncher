# YetAnotherGameLauncher

<div align="center">

<img src="docs/images/screenshot-wuthering-waves.png" alt="YetAnotherGameLauncher preview (Wuthering Waves detail page with official video backdrop)" width="100%">

**An open-source game launcher designed for Linux and Windows desktops**

Built on .NET 10 + Avalonia 12 + MVVM. Games are driven entirely by a configuration file — no specific game is hard-coded.

[![CI](https://github.com/AngelBeats-Kanade/YetAnotherGameLauncher/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/AngelBeats-Kanade/YetAnotherGameLauncher/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Linux%20%7C%20Windows-blueviolet)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=.net&logoColor=white)
![Avalonia](https://img.shields.io/badge/Avalonia-12-9B4FAB)
![Version](https://img.shields.io/badge/version-v0.1.0-blue)

[简体中文](README.md) | **English**

</div>

> English translation of [README.md](README.md). The Chinese version is the authoritative reference.

Currently supported games:

- **Wuthering Waves** (Kuro Games official launcher protocol; CN / Bilibili / Global servers)
- **Arknights: Endfield** (GRYPHLINE / Hypergryph launcher protocol; CN / Global / Bilibili servers)

> [!NOTE]
> Both games are Windows programs; running them on Linux depends on **wine / Proton**.
> This launcher does not manage wine for you — instead it supports any wrapper through
> launch command templates (see [GAME_CONFIG.md](docs/GAME_CONFIG.md)). The default umu
> launch chain even installs Proton for you automatically — see below.

## ✨ Features

- 🎮 **One-click launch** — Command templates with `{exe}` / `{installDir}` placeholders + environment variable injection; pre-launch checks report categorized, localized errors; game output is captured to a launch log; failures show a themed error card (with an open-log-folder button)
- 🐧 **Native Linux umu launch chain** — No external umu-run required: Proton and Steam Runtime are downloaded automatically per the Proton toolmanifest, with container and prefix set up for you; choose between DW-Proton (default) / GE-Proton / UMU-Proton — **selection is saved instantly**, download assets match the host architecture (x86_64/aarch64); each flavor ships a "check for updates" action that upgrades and cleans up old versions
- ⬇️ **Full download + resume** — Per-file manifest sync (Wuthering Waves) / archive extraction (Endfield) with size+MD5 verification; `.temp` files + HTTP Range resume; linear-backoff retries for transient network errors; adjustable download speed limit
- 🩹 **Incremental updates + pre-download** — Wuthering Waves uses official krpdiff patch packages applied via native `hpatchz` (HDiffPatch) with `.yagl-bak` backup rollback; two-phase pre-download (stage first, apply when official servers open); package-based channels can register an already-installed game with zero downloads
- 🧰 **Verify & repair** — Post-install manifest verification (MD5) that automatically repairs missing/corrupted files and cleans orphaned files (keeping `Saved/` game saves)
- 🖥️ **One-click server switching** — Wuthering Waves CN/Bilibili/Global, Endfield CN/Global/Bilibili, all configuration-driven
- 🎞️ **Poster-style detail page + video backdrop** — Official artwork/video fills the main area; FFmpeg hardware decoding (D3D11VA on Windows / VAAPI→NVDEC on Linux) with seamless loop points
- 💌 **Wish (gacha) records** — Wuthering Waves: automatic in-game URL extraction, official API fetching, local caching and pity statistics
- 🌗 **Themes / i18n** — Light / dark / follow-system themes; Simplified Chinese and English UI, switching takes effect immediately
- 🧭 **Native Wayland first** — On Linux with a Wayland session, the experimental Avalonia 12.1 native Wayland backend is used (compositor-provided fractional scaling); fall back to X11/XWayland anytime with `YAGL_FORCE_XWAYLAND=1`

<details>
<summary>📦 Full feature matrix (click to expand)</summary>

| Feature | Description |
|---|---|
| Game launch | Command templates with `{exe}` / `{installDir}` placeholders + environment variable injection; pre-launch checks (main executable/runtime/prefix) with categorized localized errors; game output saved to launch logs (`~/.local/share/yagl/logs/`); themed error card on failure (with open-log-folder) |
| Linux launch mode selector | **umu launch / direct run** (Linux only); **umu launch** is the default (built-in C# launch chain that downloads Proton and Steam Runtime per the Proton toolmanifest and sets up container + prefix — no external umu-run needed); a **Proton flavor** picker sits next to it: DW-Proton (default, Dawn Winery build, dawn.wine) / GE-Proton / UMU-Proton — **selection is saved instantly and only the chosen flavor is downloaded**; assets are matched to the host architecture (x86_64/aarch64) so the wrong architecture is never installed; each flavor has a **check for updates** button: a confirmation dialog appears when a new version is detected, and the old version directory is cleaned up automatically after upgrading; `launch.umuId` aligns UMU_ID with the umu database ID (`umu-3513350` for Wuthering Waves, `umu-endfield` for Endfield); recommends compatibility environment variables when an NVIDIA GPU is detected; legacy wine/Proton templates keep working; on first Linux run the default `{exe}` template is upgraded to umu launch automatically (and existing `umu-run {exe}` templates are upgraded once), so launch works out of the box |
| Full download | Per-file manifest sync (Wuthering Waves) / archive extraction (Endfield), size+MD5 verified |
| Resume support | `.temp` files + HTTP Range resume; linear-backoff retries for transient network errors |
| Speed limit | Download speed can be capped in bytes/second |
| Incremental update | Wuthering Waves: matches the official `patchConfig` diff entry, downloads krpdiff patch packages, applies them with native `hpatchz` (HDiffPatch), `.yagl-bak` backup rollback |
| Pre-download | Two-phase: "pre-download" stages into `.yagl/predownload`, then a one-click "apply" once official servers open; krpdiff packages for Wuthering Waves, full packages for Endfield |
| Register version | Package-based channels can register an already-installed game with zero downloads (Endfield detected state) |
| Verify & repair | Post-install manifest verification (MD5); automatically repairs missing/corrupted files and cleans orphaned files (keeping `Saved/` saves) |
| Multi-server | Wuthering Waves CN/Bilibili/Global, Endfield CN/Global/Bilibili — one-click switching, all configuration-driven |
| Wish (gacha) records | Wuthering Waves: automatic in-game URL extraction, official API fetching, local cache and pity statistics |
| Modern UI | Borderless rounded window with custom title bar (drag region / minimize / maximize / close); poster-style detail page: official artwork fills the main area (left edge preserved, no overlay) with top-left rounded full-bleed layout and a status/version chip cluster under a gradient scrim (gold version accent); two-phase sidebar selection indicator animation; sidebar auto-collapses below a width threshold (with hysteresis); light/dark/follow-system themes; page transitions and button micro-animations; custom app background image in settings |
| Video backdrop | Detail page plays the official video backdrop: FFmpeg hardware decoding (D3D11VA on Windows / VAAPI→NVDEC on Linux), smart loop point + preroll for gapless looping; on Linux prefers the distro FFmpeg 9 (libavcodec.so.63) and auto-downloads a BtbN build to the app data directory when missing |
| Asset cache & preheat | Icons and backdrops are all cached locally: all game icons show from disk cache at startup and backdrops pre-load (without waiting for selection) — zero network; version/preload detection runs once per game per launch, and backdrops/icons are only re-fetched after the game version changes (backdrops strictly follow game versions) |
| Wine prefix | Uniformly located at `~/.local/share/yagl/prefixes/<gameId>` (the Windows-shaped STEAM_COMPAT_DATA_PATH points here too), never inside the game install directory — install sync cannot wipe it |
| UI language | Simplified Chinese / English, optional follow-system, switching takes effect immediately (settings page) |
| Proxy settings | Follow system / direct / manual |
| Auto-start on boot | Windows registry / Linux XDG autostart |
| Native Wayland | On Linux with a Wayland session (`WAYLAND_DISPLAY`), the experimental Avalonia 12.1 native Wayland backend is used: fractional scaling comes straight from the compositor (no Xft.dpi patch needed); set `YAGL_FORCE_XWAYLAND=1` to fall back to X11/XWayland (that path keeps EGL-first rendering and automatic DPI sync) |
| Launch settings | A dedicated game settings sub-page (gear icon on the detail page): location / launch mode / launch arguments, saved back to the config file; a toast appears when settings actually change (listing changed fields; nothing pops when nothing changed or validation failed), server switching also toasts |
| Visual storage paths | Change the install root in settings; each game's install directory can be changed individually in the game settings page "location" section, effective immediately on save |
| Official icons | Official app icons for Wuthering Waves/Endfield (bundled by default as `avares://YetAnotherGameLauncher/Assets/game-icons/*.jpg`; the `icon` field still accepts URLs/local paths, URL icons get a disk cache and fall back to the initial letter on failure) |
| Architecture | Layered: `Core` (domain) → `Channels.*` (vendor channels) → `App` (Avalonia UI), everything depends on abstractions |

</details>

<img src="docs/images/screenshot-arknights-endfield.png" alt="YetAnotherGameLauncher preview (Arknights: Endfield detail page)" width="100%">

## 📥 Download & run

No development environment needed — four steps (the release packages are **self-contained**,
**no .NET runtime required**):

1. **Download**: open the [Releases page](https://github.com/AngelBeats-Kanade/YetAnotherGameLauncher/releases)
   and grab the package for your system from the latest release's Assets:
   - Windows 64-bit: `YetAnotherGameLauncher-<version>-win-x64.zip`
   - Linux x86_64: `YetAnotherGameLauncher-<version>-linux-x64.tar.gz`
2. **Extract** to any directory, e.g. `D:\YetAnotherGameLauncher` on Windows or `~/YetAnotherGameLauncher` on Linux
3. **Run**:
   - Windows: double-click **`YetAnotherGameLauncher.exe`** in the extracted folder
   - Linux: in a terminal, `cd` into the extracted folder and run **`./YetAnotherGameLauncher`**
     (if it complains about the execute bit, run `chmod +x YetAnotherGameLauncher` first)
4. **Start playing**: the first launch generates a default config automatically, with Wuthering Waves and
   Endfield already in the sidebar — pick a game → click **Install Game** → wait for the download → click
   **Launch Game**

> [!TIP]
> - **No wine / Proton needed on Linux beforehand**: the default umu launch chain automatically downloads
>   DW-Proton (the default flavor; switch to GE-Proton / UMU-Proton in game settings) and Steam Runtime
> - Wuthering Waves **incremental updates** need the [hpatchz (HDiffPatch)](https://github.com/sisong/HDiffPatch/releases)
>   executable: install it and make sure it is on PATH (configuring a custom tool path is not supported yet)
> - Want the games somewhere else? Change the **install root** under "Settings → Downloads" before installing
> - The config file lives at `~/.config/yagl/games.json` on Linux and `%APPDATA%\yagl\games.json` on Windows

## 🖱️ Using the UI

Day-to-day use **never requires editing a config file by hand** — the launcher UI is a complete
configuration tool on its own.

### Game detail page: install / update / launch

Click a game in the sidebar to open its detail page. The buttons in the bottom action dock change with
the game's state:

- **Install Game** → becomes **Launch Game** once installed; becomes **Update Now** when the official side has an update
- **Verify & Repair**: available any time after install; automatically repairs missing/corrupted files (saves are kept)
- **Register version**: appears when Endfield detects an already-installed game — zero downloads
- **Preload Next Version / Apply Preload**: appear during the official pre-download window; stage first, apply in one click
- **Convene History** (Wuthering Waves only): gacha records and pity statistics
- Launch failures show an error card: **retry**, **open the log folder**; on Linux with Proton missing you can
  switch to a locally installed Proton right there

### Game settings page: per-game settings

Enter via the **gear** button at the bottom of the detail page; "Back to game" returns to the detail page:

![Game settings page](docs/images/screenshot-game-settings.png)

- **Server**: switch between CN / Bilibili / Global (each server keeps its own install directory;
  the default server is restored after restarting the launcher)
- **Location**: **install directory** and **game executable**, both with a "Browse…" button backed by the
  system file picker; if you already downloaded the game yourself, just point at its main executable —
  no re-download needed
- **Launch**:
  - **Launch mode**, either **umu launch (recommended)** or **direct run** (Linux only); on Windows the game
    starts the official default way, no compatibility layer needed
  - **Proton flavor** (Linux umu mode): DW-Proton (default) / GE-Proton / UMU-Proton — selection is saved instantly
  - **Check/download compat components** and **check for updates** (Linux umu mode): pre-download or upgrade Proton manually
  - **Command template / working directory / environment variables** (all platforms): advanced customization with
    `{exe}` and `{installDir}` placeholders

![Linux launch settings](docs/images/screenshot-launch-settings-linux.png)

Location and Proton flavor changes are saved instantly; for everything else click **Save Launch Options**
at the bottom of the launch card. A toast in the top-right corner lists the changed fields when something
actually changed.

### Global settings page: the whole app

Enter via **Settings** at the bottom of the sidebar:

![Global settings page](docs/images/screenshot-settings.png)

- **Appearance**: theme (follow system / light / dark) and UI language (Simplified Chinese / English), effective immediately
- **App background**: custom background image for the sidebar and settings pages (local file or URL)
- **Downloads**: install root (where all games install by default), download speed limit (MB/s, 0 = unlimited), auto-start on boot
- **Network proxy**: follow system / direct / custom proxy
- **Config file**: one-click open of the `games.json` folder

> [!NOTE]
> The UI covers the vast majority of everyday configuration. Only **adding new games** and tweaking
> channel parameters need a manual `games.json` edit — see the ⚙️ Configuration section below and
> [GAME_CONFIG.md](docs/GAME_CONFIG.md).

## 🧑‍💻 Quick Start (developers)

### Requirements

- .NET 10 SDK

### Build & run (development)

```bash
git clone https://github.com/AngelBeats-Kanade/YetAnotherGameLauncher.git
cd YetAnotherGameLauncher
dotnet run --project src/YetAnotherGameLauncher
```

### Release build

Regular users should just download from the
[Releases page](https://github.com/AngelBeats-Kanade/YetAnotherGameLauncher/releases)
(see "Download & run" above); to build yourself:

```bash
# Linux (self-contained; executable in publish/linux-x64/)
dotnet publish src/YetAnotherGameLauncher -c Release -r linux-x64 --self-contained -o publish/linux-x64

# Windows
dotnet publish src/YetAnotherGameLauncher -c Release -r win-x64 --self-contained -o publish/win-x64
```

### Running tests (731 tests, measured 2026-09-20 after the third review pass)

```bash
# Run the 4 test projects' compiled binaries directly (on Windows you can run the .exe;
# on some setups dotnet test discovers 0 tests):
dotnet tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll
dotnet tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll
dotnet tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Hypergryph.Tests.dll
dotnet tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll
```

Test coverage: config parsing/validation, downloader (resume/retry/MD5), manifest verification, version planning,
full sync, incremental apply (incl. rollback), package install, update orchestration, channel resolution
(Wuthering Waves/Endfield CN/Global parameters), GPU vendor detection, Wine runtime discovery (wine/Lutris) and
recommended chain, unified Wine prefix paths, pre-launch checks and categorized errors, launch log capture,
launch command parsing, Proton flavor (DW/GE/UMU) latest download with offline fallback, umuId override and
validation, umu environment alignment with upstream, localization service and language switching, sidebar
collapse/page switching/about page, theme switching, indicator dot geometry and migration choreography, detail
page layout states, glass button four-state foreground, Linux window backend decision and visual maximized
detection (Wayland tiling false-positive protection), backdrop/icon caching with version gating, startup asset
preheat and one-shot version detection, launch failure overlay, ViewModel state machines, and real-window
integration tests via Avalonia.Headless. A visual review screenshot tool is also included
(`artifacts/ui-review/`, see docs/DEVELOPMENT.md).

## ⚙️ Configuration

Almost all settings can be changed in the UI (see "Using the UI" above; changes are written back to this
file automatically). Editing the JSON by hand is the advanced path.

Config file location:

- Linux: `~/.config/yagl/games.json`
- Windows: `%APPDATA%\yagl\games.json`
- Override with the `YAGL_CONFIG` environment variable

On first run, if the config is missing, the launcher **automatically generates a default config file at the
default location** (the content is the [`samples/games.json`](samples/games.json) template: Wuthering Waves and
Endfield with three servers each (Global/CN/Bilibili) and official icons — ready to download out of the box;
an existing file is never overwritten). On Linux the generated default also upgrades the `{exe}` template to the
community-recommended chain (native umu → Proton → wine; native umu needs no external runtime — Proton and Steam
Runtime are downloaded automatically at launch) and writes compatibility environment variables; this upgrade only
happens once at first-run generation, and any user edits afterwards are never overwritten.

**Upgrading from older versions**: legacy configs are migrated automatically on first load — official servers and
localized names added to a game are filled in from the built-in template (`schemaVersion 3`); bare `{exe}`
(`schemaVersion 4`) and legacy `umu-run {exe}` (`schemaVersion 5`, a historically auto-generated form) launch
templates are upgraded to the recommended chain once each. Each migration runs only once and the status bar
reports the result. For the full field reference and an "add a new game" tutorial, see
[docs/GAME_CONFIG.md](docs/GAME_CONFIG.md).

### Example: launching via wine/Proton on Linux

```jsonc
{
  "id": "wuthering-waves",
  "displayName": "鸣潮",
  "channel": "kuro",
  "installDir": "WutheringWaves",
  "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
  "launch": {
    "commandTemplate": "wine {exe}",                 // or "steam -applaunch 0" etc.
    "environment": { "WINEPREFIX": "~/Games/wuwa-prefix", "DXVK_HUD": "0" }
  }
}
```

## 🛠️ Troubleshooting

| Symptom | Fix |
|---|---|
| The extracted binary does nothing when double-clicked on Linux | Run `./YetAnotherGameLauncher` from a terminal in the extracted folder; if the execute bit is missing run `chmod +x YetAnotherGameLauncher` first |
| Want to regenerate the default config | Delete `~/.config/yagl/games.json` (or the file `YAGL_CONFIG` points to) and restart the launcher |
| "Cannot connect to server" | Check network/proxy; the Wuthering Waves CDN is more stable inside mainland China |
| Wuthering Waves incremental update fails mentioning hpatchz | Install [HDiffPatch](https://github.com/sisong/HDiffPatch/releases) and make sure `hpatchz` is on PATH |
| Pre-download button missing | The official pre-download window is not open (Wuthering Waves `predownload.config` missing / Endfield has no `patch` node) |
| Endfield version/download errors | The GRYPHLINE protocol is undocumented and fields may change when the official launcher updates — issues welcome |
| Launch fails on Linux | A themed error card appears: retry umu component downloads or pick a locally installed Proton; use "open log folder" to inspect `launch-*.log`, or check the command template and runtime in the game settings page. On Windows a direct `{exe}` works |
| UI tiny/blurry on Linux | Only the XWayland path has this issue: the launcher syncs the Hyprland scale into `Xft.dpi` automatically (only when unset); manual fix `xrdb -merge <<< "Xft.dpi: 160"` (value = 96 × compositor scale). The native Wayland path gets scaling from the compositor and is unaffected; if the native path misbehaves, fall back with `YAGL_FORCE_XWAYLAND=1` |
| Wuthering Waves backdrop missing | Normal when no backdrop has been published officially (offline it falls back to the last successful cache, otherwise the theme gradient); first video playback downloads the FFmpeg library (~50 MB; falls back to the static poster on failure) |

## 📖 Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — architecture overview, module dependency graph, all core flow diagrams (mermaid)
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) — development guide: environment, TDD workflow, test layout, how to add channels/games
- [docs/GAME_CONFIG.md](docs/GAME_CONFIG.md) — games.json configuration reference and tutorial

## 🗂️ Project layout

```
YetAnotherGameLauncher.slnx
├── Directory.Build.props / Directory.Packages.props   # central package management
├── global.json                                        # enables MTP mode for dotnet test
├── src/
│   ├── YetAnotherGameLauncher.Core/                   # domain layer (download/manifest/sync/incremental/package/orchestration/launch)
│   ├── YetAnotherGameLauncher.Channels.Kuro/          # Kuro channel (Wuthering Waves) + hpatchz patcher
│   ├── YetAnotherGameLauncher.Channels.Hypergryph/    # GRYPHLINE channel (Endfield)
│   └── YetAnotherGameLauncher/                        # Avalonia UI (MVVM)
├── tools/IconGen/                                     # app icon generator (original anime artwork, headless render → multi-size ICO)
├── tests/                                             # xunit.v3 (MTP) + Avalonia.Headless
├── samples/games.json                                 # sample config
└── docs/                                              # development/architecture/config docs
```

## 🙏 License & acknowledgements

- This project is open source under the [MIT License](LICENSE)
- The native Linux umu launch chain (prefix layout / Steam compatibility environment / runtime discovery and download) re-implements the behavior of [umu-launcher](https://github.com/Open-Wine-Components/umu-launcher) in C#
- The download/update/pre-download flows and backdrop configuration protocol (launcher ops config) reference and credit [timetetng/wutheringwaves-cli-manager](https://github.com/timetetng/wutheringwaves-cli-manager) (Wuthering Waves protocol reverse engineering)
- The Endfield protocol references [LLauncher](https://github.com/AugustLigh/LLauncher) and [ak-endfield-api-archive](https://github.com/daydreamer-json/ak-endfield-api-archive)
- The patch tool is [HDiffPatch](https://github.com/sisong/HDiffPatch) (MIT)
- Video backdrop decoding is based on [FFmpeg](https://ffmpeg.org) (LGPL-2.1+, LGPL shared builds, bound via [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen)); the native library cache provisions the `ffmpeg-Builds LGPL` build and extracts it after verification; archive extraction uses [SharpCompress](https://github.com/adamhathcock/sharpcompress) (MIT)
- This project is a community tool, not affiliated with Kuro Games or Hypergryph in any way
