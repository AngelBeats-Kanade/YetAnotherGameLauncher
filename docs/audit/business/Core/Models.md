# Core/Models 逐行审计（12 文件，全部 ✅，2026-09-19）

审计方法：逐文件通读 + 行级覆盖数据（artifacts/coverage/phase2/UNCOVERED.md 无本目录条目 = 100% 行覆盖）+ JSON 往返与消费方测试证据。本目录全部为纯数据载体（POCO/record/enum，零逻辑行），正确性 = 属性默认值契约 + 序列化契约。

| 文件 | 行数 | 判定 | 论证与覆盖证据 |
|---|---:|---|---|
| AppSettings.cs | 44 | ✅ | 13 个属性：窗口三项经 PersistWindowState 往返测试；SchemaVersion 经迁移测试（4→5 语义）；ProxyMode/ProxyAddress 经 GameItemActionsTests.ProxyRadios 落盘往返；DownloadSpeedLimitBytes/SidebarExpanded/AppBackgroundImage 默认值经 GameCatalogJsonTests 默认断言 + 设置页保存路径间接落盘。枚举三值经 NetworkProxyManagerTests 全覆盖 |
| LaunchOptions.cs | 23 | ✅ | CommandTemplate/WorkingDirectory 默认值经 GameCatalogJsonTests.Deserialize_MinimalJson 断言；UmuId 经 NativeUmuLaunchRoutingTests（umu-3513350 保留回归）；Environment 经多测试往返 |
| GameDefinition.cs | 35 | ✅ | 9 属性全经 GameCatalogJsonTests.Deserialize_FullJson 逐字段断言；NameLocalized 回退语义经 GameDisplayNameTests |
| GameManifest.cs | 38 | ✅ | ManifestFile/PatchGroup record 构造被全部服务测试实例化并逐字段断言；EntriesAreArchives 经包式渠道测试（GameUpdateServiceTests/PackageInstallerServiceTests）双态覆盖 |
| GameServer.cs | 14 | ✅ | Options 字典经 KuroChannelApiTests（indexUrl）/GryphlineChannelApiTests（apiBase+appcode）真实消费 |
| ChannelVersionInfo.cs | 20 | ✅ | 五属性经 KuroChannelApiTests.GetVersionInfo 与 GryphlineChannelApiTests 预下载双态逐字段断言 |
| GameCatalog.cs | 11 | ✅ | Settings/Games 往返（SaveThenLoad_RoundTrips） |
| LocalGameState.cs | 14 | ✅ | 三属性经 LocalStateService 保存/加载往返（GameUpdateServiceTests/DetailPresentationTests） |
| UpdatePlan.cs | 17 | ✅ | Strategy 两态 + From/ToVersion 经 UpdatePlannerTests 决策表全覆盖 |
| UpdateProgress.cs | 21 | ✅ | Phase 六值中 Done 经 GameInstallServiceTests.ReportsDoneProgress 断言；record 被进度管线消费 |
| ThemeMode.cs | 9 | ✅ | 三值经 GameCatalogJsonTests（System 默认/Dark）与 ThemeHeadlessTests（Dark/Light/System 双断言） |
| DownloadRequest.cs | 8 | ✅ | 四元组经 HttpFileDownloaderTests 全部 17 用例实例化 |

结论：Core/Models 0 未覆盖行、0 可疑行。全部 ✅。
