# InstallPathTests 审计

- 方法数：3；判定：✅ 3

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Resolve_AbsoluteInstallDir_IsUsedAsIs (:8) | ✅ | 绝对安装目录原样使用（GetFullPath 归一） |
| Resolve_RelativeInstallDir_IsRelativeToRoot (:18) | ✅ | 相对目录相对安装根解析 |
| ExpandUserPath_TildeExpandsToUserProfile (:27) | ✅ | ~ 展开用户目录（~、~/Games、纯路径不误展开） |
