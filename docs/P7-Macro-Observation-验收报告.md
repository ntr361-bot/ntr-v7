# P7 Macro Observation 验收报告

## 结论

P7 已实现为纯旁路、确定性、动态 N 专家的 `MacroObservationEngine`。它只接收调用方提供的 `PrefixContext` 和 P5 版本化注册表，输出 `MacroObservationSnapshot`，没有预测、调权、学习、写库或生产入口。

P6 冻结快照存储仍未实现。因此 P7 当前能验证传入前缀是否合法并完成观察，但不负责从正式历史自动构造 `PrefixContext`，也没有接入每日预测。

## 已实现输入安全检查

- `TargetIssue` 必须严格晚于 `CutoffIssue`。
- 目标期和未来期开奖结果一律拒绝。
- `AvailableAt` 晚于观察时刻的数据一律拒绝。
- 历史专家快照必须在对应期开奖前生成并可用。
- 快照的 `HistoryCutoffIssue` 必须早于自身目标期，且不能越过当前截止期。
- 每个快照必须包含 12 个无重无漏的合法生肖。
- 同一 `ExpertId + ExpertRevisionId + TargetIssue` 的重复快照拒绝。
- 同一专家同一期出现多个 Revision 的冲突快照拒绝。
- Included 专家必须绑定一个在观察时刻已经登记的明确 Revision。

## 已实现专家观察

每个冻结 Revision 分开统计最近 10/20/50/100 个共同已揭晓样本：

- `Top3Rate = ActualRank <= 3 的样本数 / 有效样本数`
- `Top6Rate = ActualRank <= 6 的样本数 / 有效样本数`
- `MRR = mean(1 / ActualRank)`
- `MeanRank = mean(ActualRank)`
- `RankingChangeRate = 相邻两次完整排名发生变化的次数 / 可比较相邻次数`
- `UniqueRescue = 本专家进入 Top6 且该期所有有快照的对照专家均未进 Top6`
- `Top6UniqueContribution = UniqueRescue / UniqueRescueOpportunities`

窗口不足时使用真实有效样本，并保存 `EffectiveSamples` 与 `SourceIssueRange`；没有样本时值为 `null`，不是 0。

## 已实现专家依赖和分歧观察

- P5 中的 `SharedHistory`、`SharedFeatures`、`ConsumesExpertRanking` 等原始边分别保留。
- 只有消费边会把 `ParentDependency` 标记为 true；共享关系不会伪装成父子关系。
- 依赖组由本期冻结 Revision 的真实连接分量形成。
- 两专家每期完整 12 生肖排名使用标准 Spearman 相关系数。
- `Top6Overlap = 两专家 Top6 交集数量 / 6`。
- `ResidualDiversity = 1 - Top6Overlap`，仅为观察指标。
- `EffectiveIndependentExpertCount` 保持 `null`，没有未经验证的公式，更不会用于权重。

## 已实现环境事实

- `ImmediateRepeatRate`：当前已揭晓序列与前 1 期相同生肖的比例。
- `Gap1RepeatRate`：与前 2 期相同生肖的比例。
- `Gap2RepeatRate`：与前 3 期相同生肖的比例。
- `ZodiacConcentration = Σ p(zodiac)^2`（HHI）。
- 每个生肖 10/20/50/100 窗口频率及相对 100 样本基准的差值。
- 每个生肖当前遗漏：从最新已揭晓结果向前计数。
- 当前冻结专家的平均 Spearman 与平均 Top6 overlap。

冷热结构目前以可复核的短中期频率、相对 100 样本变化、遗漏和集中度原始事实表达。没有预注册并经训练区间冻结的阈值前，不擅自贴 `RecentHot`、`DeepCold` 等结论标签。

## 明确保持未知的内容

以下项目缺少本阶段所需的冻结输入或对照定义，P7 不伪造：

- 趋势因子兑现率：`TrendReliability = null, EffectiveSamples = 0`。
- `ContextReliability`。
- Rescue/Harm/NetImpact：没有冻结 Control Ensemble 时为 `null`。
- MarginalRescue/Harm/NetImpact：契约对象保留，但 `CommonSamples = 0`，并标记 unavailable。
- `DependencyPenalty`、`RedundancyPenalty`。
- `SignedReliability` 与专家正向/负向状态；专家状态保持 `NeutralExpert`。

## 可复现性

`SnapshotHash` 绑定：运行身份、目标期、截止期、AsOf、来源清单、完整 ExpertPool、全部事实、全部专家观察、依赖组、成对统计及原始依赖边。相同输入产生相同哈希；来源清单改变会改变哈希。

## 与正式软件的隔离

- 未修改正式 V7、V6.5-50、V6.5-100、V6.5-All 或任何 AutoLearning 算法。
- 未改变正式 Top3/Top6。
- 未修改 17 期三专家实验。
- 未实现 Gating、COUNTER、Hypothesis、Evidence、Critic、Confidence 或权重学习。
- 未接入每日工作流、UI、云端或生产数据库。
- 未进入 P8。

