# P16–P20 Macro Reasoning 旁路开发报告

## 结论

P16–P20 已形成可执行的旁路闭环：冻结专家快照经过反事实排名，预测审计先于开奖追加保存；开奖可见后执行反思，使用事务和防重复凭据更新版本化推理记忆；P20 按时间顺序重复该过程，并把 Training、Validation、Holdout 分开统计。

本阶段没有读取真实 `history.db`，没有运行 300/600/1000 期历史验证，没有接入生产，没有启用专家或 COUNTER，也没有修改正式 V7/V6.5、每日工作流、PredictionHistory 和 Top6。

## 各阶段

- P16 `MacroCounterfactualEngine`：同一批不可变快照产生 Base、NoAction、Proposed、Applied 四套完整12生肖排名；只支持 FOLLOW/IGNORE，拒绝在本阶段启用 COUNTER。
- P17 `MacroReasoningAuditStore`：使用独立 SQLite 文件保存 Run、Prediction、Reflection、Memory 和学习凭据；预测与推理记录只追加，更新和删除由数据库触发器拒绝；相同期号不同内容拒绝覆盖。
- P18 `MacroReflectionEngine`：开奖结果在预测后可见时才评价 RESCUE/HARM/NO_IMPACT；假设判断和动作判断分开。10期成熟度按实际已揭晓序列计数，不用“期号+10”冒充开奖日历。
- P19 `MacroReasoningMemory`：反思与记忆在同一事务提交；按 AuditHash、HypothesisId、阶段防止重复学习；Pending/Indeterminate 不计作错误；ReadAsOf 同时限制目标期和可见时间。
- P20 `MacroWalkForwardEvaluator`：每期按 Reveal→Reflect→Memory→Observe→Hypothesis→Evidence→CounterEvidence→Critic→Confidence→Decision→Uncertainty→Counterfactual→Audit 的顺序运行。动作策略、参数哈希、历史模式和时间切分必须事先冻结。

## 指标

P20 输出 Top1/Top3/Top6、MRR、Mean/Median Rank、最大连续 Top3/Top6 未中、Control 对照、Rescue/Harm、Apply/Hold/Reject、HypothesisAccuracy、DecisionAccuracy、置信度分层准确率、过度/不足置信、CriticPreventedHarm 和 CriticMissedRescue。无法判定的数据返回 null 并保存分母，不把缺失当作0分。

## 已知边界

- 当前默认策略是冻结的 NoAction/HOLD 对照，不会自行追逐近期胜出模型。
- P11 的环境相似性与排名影响预览仍明确为 unavailable，因此真实链最多 CAUTION；未伪造 PASS。
- P18 V1 只有具备可复算未来观测定义的假设才进入正确率分母；其余为 Indeterminate。它不把“解释不了”记成预测错误。
- P20 是评估执行器，不等于 P21 的真实历史成绩。只有后续按已冻结方案执行 300/600/1000、Holdout 和 Live Shadow 后，才能评价模型是否改善预测。

## 技术验收

使用12个合成目标期、100个合成历史前缀跨年份运行。验收覆盖：完整四专家池、审计哈希、HOLD不改排名、先开奖后反思、10个真实序列样本成熟、记忆AsOf隔离、幂等追加、冲突覆盖拒绝、重放禁止使用未来记忆、分区统计、重复运行哈希一致，以及正式V7/V6.5输出不变。该测试只证明结构闭环与隔离，不代表历史预测收益。
