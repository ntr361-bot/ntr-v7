# P9-P14 Macro Reasoning 统一验收报告

验收日期：2026-09-09

验收范围：P9 Evidence、P10 Counter-Evidence、P11 Critic、P12 Confidence、P13 Decision、P14 Gating。

边界：旁路研究链，不接生产，不改变正式 V7/V6.5，不学习权重，不发布云端。

## 结论

P9-P14 已形成可执行的结构化旁路链：冻结观察与假设进入支持证据、主动反证、独立批判、受限置信度评分，生成不可变的 APPLY/HOLD/REJECT 决策记录；P14 再对已经批准的动态 N 专家权重进行纯排名聚合。

当前链不会修改专家权重、正式生肖排名或历史记录。P13 只生成决策记录，P14 只返回内存中的完整12生肖排名；生产预测、PredictionHistory、每日工作流和正式 Top6 均未接入或改写。

## 分阶段验收

### P9 MacroEvidenceEngine

- 版本：`p9-evidence-v1`。
- 从 P7/P8 已声明事实构造可复现的 SupportingEvidence。
- ColdReturnRate、TrendReliability 等没有事实来源的信号保持 Missing，不猜值。
- EvidenceId 绑定假设、来源范围、数值及定义版本。

### P10 MacroCounterEvidenceEngine

- 版本：`p10-counter-v1`。
- 主动检查 50/100 期不确认、相反方向和结构性反证。
- 空假设同样接受挑战；未完成支持搜索或未来数据会被拒绝。

### P11 MacroReasoningCritic

- 版本：`p11-critic-v1`。
- 检查前缀安全、假设/空假设、支持/反证完整性、权重、样本、短窗追涨、长期冲突及动作幅度。
- 非法输入、泄漏、缺反证、非法权重或动作超过 5% 时 REJECT。
- 冻结契约尚未把 P14 预览结果和已验证环境相似度传回 P11，因此真实链仍明确记录 unavailable，最多 CAUTION，不伪造 PASS。

### P12 MacroConfidenceEngine

- 版本：`p12-confidence-score-v1`。
- 综合支持、反证、样本、历史可靠度、Critic 和校准质量。
- 输出是受限分数，不宣称为已校准概率；搜索不完整或 Critic REJECT 时为 0。

### P13 MacroDecisionEngine

- 版本：`p13-decision-v1`。
- 生成不可变决策，不产生生肖排名、不写数据库。
- 无假设、缺空假设、证据不完整、低置信度或无实际动作时 HOLD。
- Critic REJECT 强制 REJECT；CAUTION 的动作幅度最多 1%。

### P14 MacroGatingModel

- 版本语义：P14 V1 weighted Borda。
- 支持动态 N 专家，权重结构不是固定4槽位。
- 快照、决策、权重必须具有完全一致的 ExpertId 支持集和 Revision。
- 每个输入排名必须是12生肖无重无漏完整排列；AsOf 时尚不可见的快照被拒绝。
- FOLLOW 使用原冻结排名；IGNORE 必须为0权重；COUNTER只能消费显式匹配的不可变视图，禁止隐式负权重。
- `full12-rank-reversal-v1` 只做视图完整性验证，不代表统计准入已经通过；本次没有把任何真实专家改为 COUNTER。
- 聚合公式为 `Σ weight × (13-rank)`，平分使用固定生肖次序，结果不受输入枚举顺序影响。

## 验证证据

- Macro 契约：13项契约检查通过。
- P5 Expert Registry：通过。
- P6 Immutable Snapshot：通过。
- P7 Observation：通过。
- P8 Hypothesis：通过。
- P9-P13 专项：51个显式断言/拒绝场景通过。
- P14 专项：17个显式断言/拒绝场景通过。
- 完整 Smoke 回归：退出码0。
- Release 编译：退出码0，0个错误，1个警告。
- 正式 V7 Top6：P9-P13 专项回归已验证不变；P14 无生产引用。

## 未实现且明确留到后续阶段

- 把 P14 排名预览安全地纳入 P11 Critic 的后续版本。
- P15 Uncertainty Controller。
- P16 Counterfactual 四套排名。
- P17 Prediction/Decision Audit 持久化。
- P18 开奖后 Reflection。
- P19 Reasoning Memory 更新。
- 真实 COUNTER 统计准入、正式调权、云端发布或生产开关。

本次验收只证明 P9-P14 旁路结构、边界检查、决策记录与纯排名聚合可执行；不宣称预测成绩改善，不允许据此上线。
