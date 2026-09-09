# V65 基础专家 Macro 准入适配设计

## 目标

为 `V65-50`、`V65-100`、`V65-All` 建立只用于 Macro 旁路的完整排名适配和不可变快照入口，使其达到 P6 技术准入标准，同时保持现有 V6.5 正式评分、排序、输出、权重和生产调用链不变。

## 设计

新增一个共享实现的 `V65BaseMacroExpertAdapter`，但为三个窗口各定义独立、不可变的专家修订和算法版本。适配器只接受调用者传入的 `HistoryRecord` 前缀，按专家定义选择固定窗口和 `V65ExperimentPipeline.GetWeightsForPeriods`，再直接调用现有 `V65RuleScoringEngine.Predict`。它不读取数据库、`PredictionHistory`、`ModelMemory` 或目标期开奖结果，也不复制或改写评分公式。

新增 `V65BaseMacroExpertSnapshotService`。服务验证 `HistoryCutoffIssue < TargetIssue`、传入历史全部不晚于截止期且最大期号等于截止期，随后生成完整无重无漏的 12 生肖排名，并调用 `ImmutableExpertSnapshotStore.FreezeAndAppend`。历史因果重建使用同一适配器，额外保存前缀哈希、模拟时点、重建时间和代码版本；不伪装为历史实时快照。

## 版本

- `V65-50@macro-full12-f016-t016-o020-h016-p032-c000-v1`
- `V65-100@macro-full12-f024-t013-o016-h020-p027-c000-v1`
- `V65-All@macro-full12-f017-t017-o015-h017-p034-c000-v1`

对应 `AlgorithmVersion` 使用同样的窗口与权重编码，且不包含 `unknown` 或 `current`。旧修订保留，新修订只追加。新修订的 `LeakageAuditStatus` 与 `SnapshotIntegrityStatus` 在全部测试通过后登记为 `Passed`，但 `EligibleForMacro=false`、`Enabled=false`。

## 验证边界

测试覆盖三个专家的确定性、完整排名、正式排名一致性、正式输出不变、哈希校验、幂等追加、冲突覆盖拒绝、未来前缀拒绝和历史因果重建。测试还验证 `PredictionHistory` 不发生变化。此阶段不处理 Auto 模型、不实现 P15+、不接生产、不改正式权重。
