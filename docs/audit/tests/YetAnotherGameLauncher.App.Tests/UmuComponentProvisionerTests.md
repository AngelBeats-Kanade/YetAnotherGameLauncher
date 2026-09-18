# UmuComponentProvisionerTests 审计

- 方法数：22；判定：✅ 22
- 形态：直调；StubHttpHandler + FakeDownloader 全替身；真实 tar.gz 构造（Pax 条目与线上一致）；
  路径断言两侧统一 NormalizeSeparators（:406，正面包样）；hostArchitecture 注入实现双架构确定测试。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| EnsureProtonAsync_DWCodename_DownloadsForgejoLatestAndStripsArchSuffix (:46) | ✅ | DW 源（Forgejo 数组根）分流 + 干扰资产跳过 + 目录名剥架构后缀 |
| EnsureProtonAsync_GECodename_UsesGitHubLatestObjectJson (:69) | ✅ | GE 源（GitHub 对象根）分流 |
| EnsureProtonAsync_UMUCodename_UsesUmuRepoLatest (:88) | ✅ | UMU 源分流 |
| EnsureProtonAsync_LocalInstalled_ReturnsWithoutNetwork (:106) | ✅ | 本地已装即用不联网（零下载断言） |
| EnsureProtonAsync_DWCodename_UsesLocalDwproton (:120) | ✅ | DW 本地目录复用 |
| SelectTarAsset_DualArchAssets_PrefersHostArchSuffix (:132) | ✅ | 资产顺序与架构无关（GE ARM 排前事故回归），双主机架构断言 |
| SelectTarAsset_UnsuffixedAsset_WorksWithAnyHost (:153) | ✅ | 无后缀资产任意主机可用 |
| SelectTarAsset_AssetNameWithPathCharacters_Rejected (:171) | ✅ | release 名含 ../ / 首点在选择阶段拒绝（纵深防御） |
| SelectTarAsset_OnlyOppositeArch_ReturnsEmpty (:190) | ✅ | 仅反向架构 → 空 URL（调用方报错不下载） |
| EnsureProtonAsync_GECodename_DualAssets_DownloadsHostArchAsset (:206) | ✅ | 端到端：双资产下载的正是主机架构（请求列表精确断言） |
| EnsureProtonAsync_GECodename_OnArm64Host_DownloadsAarch64Asset (:225) | ✅ | 注入 Arm64 主机下载 aarch64 |
| EnsureProtonAsync_WineserverArchMismatch_AbortsAndCleans (:244) | ✅ | 无后缀名骗过过滤时 wineserver ELF e_machine 兜底：抛 LaunchException + 目录已清理 |
| EnsureProtonAsync_WineserverArchMatches_Installs (:264) | ✅ | ELF 匹配正常安装 |
| FindInstalledProton_WrongArchLocal_Skipped_CorrectArch_Found (:279) | ✅ | 错架构本地目录视同缺失（代号前缀与版本名两条路径）+ 换对架构恢复 |
| EnsureProtonAsync_PreinstalledWrongArch_SelfHealsByRedownload (:296) | ✅ | 事故自愈端到端：错架构重下替换 + 结果 ELF 为本机架构 |
| FetchLatestProtonTagAsync_ResolvesCodenameToUpstreamTag (:319) | ✅ | 三代号 tag 解析 |
| FetchLatestProtonTagAsync_UnsupportedCodename_Throws (:331) | ✅ | 未知代号抛 LaunchException（ProtonDownloadFailed） |
| EnsureRuntimeAsync_CatalogFetchFails_ClassifiesAsUmuRuntimeDownloadFailed (:340) | ✅ | runtime 元数据失败归类 UmuRuntimeDownloadFailed（防裸 HttpRequestException 丢重试 UI 回归） |
| UpdateProtonAsync_InstallsLatestAndPrunesSameFlavorOldVersions (:351) | ✅ | 更新装最新 + 清同发行版旧版 + 保留其它发行版 |
| UpdateProtonAsync_AlreadyLatest_ReturnsExistingWithoutDownload (:373) | ✅ | 已最新零下载返回现有 |
| ParseSha256For_FindsMatchingArchiveLine (:389) | ✅ | SHA256SUMS 行匹配 |
| ParseSha256For_MissingFile_ReturnsEmpty (:400) | ✅ | 缺失文件返回空串 |
