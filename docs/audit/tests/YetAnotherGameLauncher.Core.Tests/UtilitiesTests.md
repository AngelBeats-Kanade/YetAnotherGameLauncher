# AutostartContentTests / FileUtilitiesTests / HashingTests 审计

## AutostartContentTests（2 ✅）

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| BuildDesktopContent_QuotesExecPath (:9) | ✅ | desktop 内容格式（Exec 引号/Type/节头） |
| DesktopFilePath_UnderXdgAutostart (:19) | ✅ | 路径 = ~/.config/autostart/*.desktop |

## FileUtilitiesTests（4 ✅）
跨平台"删不掉"双路复现（Windows FileShare.None 句柄 / Linux 去写权限），双腿各有真实断言。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| TryDeleteDirectory_NestedTree_RemovesEverything (:17) | ✅ | 深树全删 |
| TryDeleteDirectory_Missing_ReturnsTrue (:29) | ✅ | 缺失幂等 true |
| TryDeleteDirectory_UndeletableChild_ToleratesAndReturnsFalse (:35) | ✅ | 尽力删除语义：残留保留 + 兄弟已清 + false |
| WriteAtomicAsync_ReadOnlyTarget_Overwritten (:71) | ✅ | 原子写解除只读属性后覆盖（Windows 占用语义防线） |

## HashingTests（2 ✅）

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Md5Hex_Bytes_KnownVector (:10) | ✅ | 标准测试向量 MD5("abc") |
| Md5Hex_File_MatchesStreamHash (:19) | ✅ | 文件流哈希与内存哈希一致（随机 4KB） |
