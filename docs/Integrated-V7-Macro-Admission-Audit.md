# Integrated-V7 Macro Admission Audit

## Verdict

`Integrated-V7@macro-full12-f055-o045-v1`: **PASS（技术准入通过，仍未启用）**

旧修订 `Integrated-V7@f055-o045-v1` 原样保留。旧修订仍只代表正式 V7 的硬过滤输出，不宣称具有完整12生肖排名。

## Evidence

| 检查项 | 结果 | 证据 |
|---|---|---|
| 完整12生肖排名 | Passed | 适配器从 `FeatureEngine` 读取固定12生肖，输出前执行无重无漏校验；0/1/2个硬排除测试通过。 |
| 正常生肖保持正式顺序 | Passed | 使用正式 Integrated-V7 相同底层分数及 `Score desc + Zodiac ordinal` 排序；逐场景与正式概率排序对照通过。 |
| 硬排除尾置 | Passed | 一个排除固定第12；两个排除按底层分数占第11/12；测试通过。 |
| 确定性 | Passed | 同一历史输入重复运行得到相同完整排名。 |
| TargetIssue / HistoryCutoff | Passed | 快照服务要求 `HistoryCutoffIssue < TargetIssue`，拒绝无效期号、超过截止期的数据和声明截止期与实际最大期号不一致。 |
| Future leakage | Passed | 适配器只消费调用者传入的历史前缀；不读取当前数据库、PredictionHistory、开奖结果、ModelMemory或其他专家输出。 |
| P6 Snapshot Integrity | Passed | 完整排名经 `ImmutableExpertSnapshotStore.FreezeAndAppend` 密封，哈希可验证、可读取、同内容幂等、冲突内容不可覆盖。 |
| 正式 V7 Top6/概率 | Unchanged | 0/1/2排除场景均在适配前后比较正式 Top6 和概率字典，完全一致。 |
| PredictionHistory | Unchanged | 冻结前后 PredictionHistory 序列化内容一致。 |
| 生产接入 | No | DailyPredictionAutomation、V7PredictionHistoryService、V7PredictionEngines 均未接入或修改。 |

## Revision

- ExpertId: `Integrated-V7`
- ExpertRevisionId: `Integrated-V7@macro-full12-f055-o045-v1`
- AlgorithmVersion: `integrated-v7-macro-full12-f055-o045-v1`
- SnapshotType: `FullRanking12`
- Inputs: `history.db`, `FeatureEngine`, `IntegratedV7MacroExpertAdapter`
- LeakageAuditStatus: `Passed`
- SnapshotIntegrityStatus: `Passed`
- EligibleForMacro: `false`
- Enabled: `false`

## Isolation conclusion

该修订已经具备以后进入 P6/P7-P14 专家池的技术资格，但本阶段没有把它加入任何冻结Pool，没有启用Gating，也没有改变正式预测。后续启用仍需单独人工确认。
