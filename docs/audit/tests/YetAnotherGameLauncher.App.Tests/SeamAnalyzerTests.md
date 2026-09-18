# SeamAnalyzerTests 审计

- 方法数：12；判定：✅ 12
- 形态：纯函数直调（缩略降采样/MAD/循环点搜索/硬切边界），构造帧确定性强。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Downsample_WhiteFrame_ProducesFullWhite (:27) | ✅ | 全白帧降采样尺寸与值 |
| Downsample_PureBlue_UsesBt601LumaWeights (:38) | ✅ | BT.601 亮度权重（纯蓝=29） |
| Downsample_HorizontalGradient_IsMonotonicAcrossX (:49) | ✅ | 渐变降采样单调性 |
| Mad_IdenticalThumbs_IsZero (:77) | ✅ | 同帧 MAD=0 |
| Mad_ConstantOffset_EqualsOffset (:85) | ✅ | 恒偏移 MAD=偏移 |
| Mad_LengthMismatch_Throws (:94) | ✅ | 长度不匹配抛 ArgumentException |
| FindLoopPoint_PicksMatchingPair (:101) | ✅ | 头尾相同帧对命中（索引+差异 0） |
| FindLoopPoint_RejectsPairsShorterThanMinLoop (:118) | ✅ | 低于最小循环时长拒绝 |
| FindLoopPoint_NoMatchAboveThreshold_ReturnsNull (:130) | ✅ | 超阈值无命中 |
| FindLoopPoint_TieBreaksToEarliestHead (:142) | ✅ | 同分取最早头帧（循环更长） |
| FindLoopPoint_EmptyInput_ReturnsNull (:157) | ✅ | 空输入双侧 |
| ShouldHardCut_ThresholdBoundary_IsExclusive (:164) | ✅ | 阈值边界开区间语义 |
