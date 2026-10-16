# Third-Party Notices / 第三方组件声明

YetAnotherGameLauncher 在运行时使用以下第三方组件与项目成果，感谢原作者。

## Runtime dependencies / 运行时依赖

| Component | License | Scope |
| --- | --- | --- |
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) 12.1.3 | MIT | UI framework |
| [Avalonia.Fonts.Inter](https://github.com/AvaloniaUI/Avalonia) 12.1.3 | MIT (package); Inter typeface under SIL OFL-1.1 | UI font |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4.2 | MIT | MVVM tooling |
| [.NET 10](https://dotnet.microsoft.com) / Microsoft.Extensions.* | MIT | Runtime & DI |
| [HDiffPatch](https://github.com/sisong/HDiffPatch) (hpatchz) | MIT | Incremental patching (external tool; the binary provided at runtime is a community build from [ww-manager releases](https://github.com/timetetng/wutheringwaves-cli-manager/releases), v4.8.0, MD5-pinned) |
| [FFmpeg](https://ffmpeg.org) (libavcodec / libavformat / libavutil / libswscale / libswresample) | LGPL-2.1+ | Background video decoding |

### FFmpeg

本项目通过动态链接（非修改、非静态链接）方式使用 FFmpeg LGPL 共享构建，仅用于解码官方启动器投放的背景视频。
首次播放时启动器会从 [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases)（LGPL-shared 变体，SHA256 校验后解压）
获取与 [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) 绑定配套的原生库，或复用系统已安装的同版本 FFmpeg。

本应用以 LGPL 合规方式使用 FFmpeg；FFmpeg 的完整源码与许可见 https://ffmpeg.org/legal.html 。
若你分发本应用，请保留本声明并允许用户替换上述 FFmpeg 库。

| Component | License | Scope |
| --- | --- | --- |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | MIT | Extracting downloaded FFmpeg archives |
| [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) | MIT (8.0.0+; 7.x and older were LGPL-3.0) | FFmpeg C API bindings |

### Proton 发行版与 Steam Linux Runtime

首次使用 umu 启动链时，启动器会按需下载 Proton 发行版（DW-Proton / GE-Proton / UMU-Proton）与
Steam Linux Runtime 到本机使用（**不由本仓库分发**）；来源与许可证以各上游仓库为准。

## Referenced community projects / 参考的社区项目

- [timetetng/wutheringwaves-cli-manager](https://github.com/timetetng/wutheringwaves-cli-manager) — 鸣潮协议逆向：下载/更新/预更新流程与启动器背景配置协议（switch.json）
- [AugustLigh/LLauncher](https://github.com/AugustLigh/LLauncher) 与 [daydreamer-json/ak-endfield-api-archive](https://github.com/daydreamer-json/ak-endfield-api-archive) — 终末地（GRYPHLINE）协议逆向
