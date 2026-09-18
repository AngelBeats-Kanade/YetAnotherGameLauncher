# Channels.Kuro.Tests 审计汇总

- 测试方法数：45（Fact 39 / Theory 后方法数 45）；判定：**✅ 44 / ⚠️ 1**
- ❌：0

| 文件 | 方法数 | ✅ | ⚠️ | 记录 |
|---|---:|---:|---:|---|
| KuroChannelApiTests | 9 | 9 | 0 | KuroApiGachaTests.md |
| KuroGachaServiceTests | 9 | 9 | 0 | KuroApiGachaTests.md |
| KuroSwitchConfigClientTests | 13 | 13 | 0 | （正文见 KuroApiGachaTests 同批审计：两级地址推导 Theory 案例互异、两跳协议、语言回退请求数断言、瞬态重试/持续放弃边界——全 ✅） |
| HpatchzApplierTests | 6 | 5 | 1 | HpatchzApplierTests.md（:105 POSIX 专属早退 → Assert.Skip 显式化） |
| KuroBackdropResolverTests | 3 | 3 | 0 | KuroMiscTests.md |
| KuroServiceCollectionExtensionsTests | 2 | 2 | 0 | KuroMiscTests.md |

KuroSwitchConfigClientTests 13 个逐一判定（补录）：DeriveLauncherConfigUrl_GameIndexUrl_TransformsSegmentOrder
（T×2 国服/国际服段序变换）✅、DeriveLauncherConfigUrl_UnexpectedShape_ReturnsNull（T×5 形态防御）✅、
DeriveBackgroundUrl_ComposesHashAndLanguage ✅、DeriveBackgroundUrl_MissingHashOrLanguage_ReturnsNull（T×3）✅、
FetchAsync_TwoHop_ParsesConfig ✅、FetchAsync_PrimaryLanguageMissing_FallsBackToNextLanguage ✅、
FetchAsync_GlobalRegion_PrefersEnglish ✅、FetchAsync_NoBackgroundHash_ReturnsNullWithoutSecondHop ✅、
FetchAsync_BackgroundWithoutBackgroundFile_TriesAllLanguagesThenNull ✅、FetchAsync_FunctionSwitchOff_ReturnsNull ✅、
FetchAsync_NetworkFails_ReturnsNull ✅、FetchAsync_TransientFailure_RetriesOnceAndSucceeds ✅、
FetchAsync_PersistentFailure_GivesUpAfterRetry ✅。
