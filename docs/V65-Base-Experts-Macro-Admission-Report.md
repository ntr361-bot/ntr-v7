# V65 Base Experts Macro Admission Report

日期：2026-09-09

## 结论

| Expert | 技术准入 | LeakageAuditStatus | SnapshotIntegrityStatus | EligibleForMacro | Enabled |
|---|---|---|---|---|---|
| V65-50 | PASS | Passed | Passed | false | false |
| V65-100 | PASS | Passed | Passed | false | false |
| V65-All | PASS | Passed | Passed | false | false |

本报告中的 PASS 仅表示满足 P6 技术准入标准，不表示已经进入 Macro 专家池或生产预测。

## 不可变版本

| Expert | ExpertRevisionId | AlgorithmVersion |
|---|---|---|
| V65-50 | `V65-50@macro-full12-f016-t016-o020-h016-p032-c000-v1` | `v65-rule-full12-window50-f016-t016-o020-h016-p032-c000-v1` |
| V65-100 | `V65-100@macro-full12-f024-t013-o016-h020-p027-c000-v1` | `v65-rule-full12-window100-f024-t013-o016-h020-p027-c000-v1` |
| V65-All | `V65-All@macro-full12-f017-t017-o015-h017-p034-c000-v1` | `v65-rule-full12-window-all-f017-t017-o015-h017-p034-c000-v1` |

旧的 `@v65-rule-current` 修订保留，没有覆盖。V65-Auto 的既有依赖仍绑定旧生产修订，新 Macro 修订没有生产消费者。

## 准入证据

- 新适配器只调用 `V65RuleScoringEngine.Predict(history, period, fixedWeights)` 的显式历史入口，未读取数据库、PredictionHistory、ModelMemory 或目标期开奖结果。
- 快照入口逐条解析调用者传入的历史期号，要求 `HistoryCutoffIssue < TargetIssue`、所有记录不晚于截止期，并要求最大历史期号等于声明截止期。
- 三个窗口均输出无重无漏的完整 12 生肖排名；相同输入序列化结果一致。
- Macro 排名逐项等于原 V6.5 正式规则排序；适配器调用前后的正式完整输出序列化结果一致。
- LiveFrozen 与 CausalReconstruction 均经 `ImmutableExpertSnapshotStore.FreezeAndAppend` 密封；PayloadHash 验证通过。
- 完全相同快照重复追加幂等；相同身份但不同内容的覆盖被拒绝。
- 因果重建保存 `SimulatedAsOf`、真实重建时间、HistoryCutoff、代码版本和历史前缀 SHA-256，且按 CausalReconstruction 模式独立读取。
- 测试确认旁路快照没有写入 PredictionHistory。

## 验证结果

- V65 专项验收：PASS。
- P5 Expert Registry 回归：PASS。
- P6 Immutable Snapshot 回归：PASS。
- Integrated-V7 Macro Adapter 回归：PASS。
- 项目完整 Smoke Tests：PASS，退出码 0。
- Release 构建：PASS，0 错误；项目仍有 221 条既有警告。

## 隔离范围

未修改 `V65RuleScoringEngine`、`V65ExperimentPipeline` 的评分公式或固定权重；未修改正式 Top3/Top6、PredictionHistory、V65-Auto、V7-Auto、P15+、每日工作流或生产接入；未运行 COUNTER，也未发布云端。
