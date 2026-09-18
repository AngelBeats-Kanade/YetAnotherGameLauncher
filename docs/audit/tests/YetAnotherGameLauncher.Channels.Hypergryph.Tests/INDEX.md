# Channels.Hypergryph.Tests 审计汇总

- 测试方法数：17；判定：**✅ 17 / ⚠️ 0 / ❌ 0**

| 文件 | 方法数 | 记录 |
|---|---:|---|
| GryphlineChannelApiTests | 11 | GryphlineChannelApiTests.md |
| EndfieldBackdropResolverTests | 5 | 同文件内补录（见下） |
| HypergryphServiceCollectionExtensionsTests | 1 | 同文件内补录（见下） |

## GryphlineChannelApiTests（11 ✅）

批次代理协议 + 国服参数档案（ak-endfield-api-archive）覆盖：GetVersionInfo_ParsesLatestVersion ✅、
GetVersionInfo_PredownloadAvailable_WhenPatchHasVersion ✅、GetManifest_BuildsArchiveManifestFromPacks ✅、
GetPredownloadManifest_ReturnsPatchPackage ✅、GetPredownloadManifest_NoPatch_ReturnsNull ✅、
MissingApiBase_Throws ✅、EmptyProxyRsps_ThrowsUpdateException ✅、RequestBody_ContainsProtocolConstants ✅、
RequestBody_ChinaOptions_OverrideProtocolConstants ✅（国服实测参数集）、RequestBody_PartialOverride_KeepsOtherDefaults ✅、
RequestBody_BlankOptionValue_FallsBackToDefault ✅（空白值回退默认）。
请求体经 CapturingHandler 捕获断言（协议常量锁定的测试目的本身，非 D4 原文断言问题）。

## EndfieldBackdropResolverTests（5 ✅）

GetBackdrop_EmptyVideoUrl_ReturnsImageBackdrop ✅、GetBackdrop_VideoUrl_Present_ReturnsVideoBackdropWithImagePoster ✅、
GetBackdrop_ChineseRegion_RequestsZhCnLanguage ✅（请求体语言/参数断言）、
GetBackdrop_GlobalRegion_RequestsEnUsLanguage ✅（缺省参数回退国际服实测值）、
GetBackdrop_MissingApiBase_ReturnsNull ✅（零请求断言）。

## HypergryphServiceCollectionExtensionsTests（1 ✅）

AddHypergryphChannel_RegistersKeyedApiWithHttpClient ✅（渠道键 DI + HttpClient 注入）。
