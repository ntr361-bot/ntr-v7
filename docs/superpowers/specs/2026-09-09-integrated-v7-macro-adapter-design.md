# Integrated V7 Macro Expert Adapter Design

## Goal

为 Integrated-V7 增加完全旁路的 Macro `FullRanking12` 适配器，使 P6 可以冻结完整12生肖专家快照，同时不改变正式 V7 的候选过滤、Top3/Top6、概率、PredictionHistory 或每日生产链。

## Architecture

新增 `IntegratedV7MacroExpertAdapter`。它直接消费调用者传入的开奖前历史前缀，通过现有 `FeatureEngine` 取得12生肖特征，并使用 Integrated-V7 已有的 `0.55 × frequency + 0.45 × omission + 0.10 × short-cycle` 底层评分。正常候选先按正式 V7 的分数降序、生肖名稳定排序；`ShortForbidden=true` 的生肖统一排在正常候选之后，并按相同底层分数及同一稳定排序规则排列。

适配器只产生 Macro 专用结果，不修改 `V7PredictionResult`。新增独立快照入口把适配结果转换为 `ExpertSnapshot`，经 `ExpertSnapshotIntegrity.Seal` 后调用 `ImmutableExpertSnapshotStore.FreezeAndAppend()`。正式预测与历史保存不会引用该入口。

## Ranking Contract

- 结果固定包含鼠、牛、虎、兔、龙、蛇、马、羊、猴、鸡、狗、猪，且无重无漏。
- 非硬排除生肖始终排在硬排除生肖之前。
- 两组内部均按 Integrated-V7 原始底层分数降序排列。
- 分数相同时按现有正式排序规则 `StringComparer.Ordinal` 对生肖名排序。
- 一个硬排除生肖固定成为第12名；两个硬排除生肖按底层分数占第11、12名。
- 相同输入产生相同排名和分数。

## Snapshot Contract

快照绑定新的 append-only revision、TargetIssue、HistoryCutoffIssue、AlgorithmVersion、CodeVersion、注册表版本和输入依赖。适配入口拒绝：空历史截止期、`HistoryCutoffIssue >= TargetIssue`、不完整排名、注册Revision不匹配，以及 P6 已有的任何完整性错误。

## Admission Boundary

测试通过只证明适配器和快照完整性。只有同时证明：输入严格为目标期开奖前历史、P6哈希校验通过、算法/代码/Revision一致，才允许为 Integrated-V7 追加新Revision并在审计报告中将 Leakage 与 Snapshot 状态记为 Passed。新Revision仍保持 `EligibleForMacro=false`、`Enabled=false`。

## Non-goals

- 不修改正式 V7 预测逻辑。
- 不补写或更新 PredictionHistory。
- 不接 DailyPredictionAutomation。
- 不启用 Macro，不开发 P15，不处理两个 Auto 专家。
