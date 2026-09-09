# Macro Reasoning V1：总体架构补丁

P5前追加[Expert Decision Semantics契约补丁](Macro-Expert-Decision-Semantics.md)：动态专家按Revision审计，预留三种使用模式、边际贡献及静态基准，本轮不实现真实COUNTER或Gating。

本补丁接续原总体任务书，替换旧的Features→Drift→Gating→Weights设计。当前仅交付数据契约、接口、基础测试骨架与开发计划；不启动P5～P25功能实现，不迁移数据库，不接入每日预测。现有P0～P4代码保持原状。旧计划描述的是历史目标，不把文档所列功能自动当作当前正式链已经验收的事实；后续以现存独立学习版本、收据和测试作为接入依据。

## 运行链与职责

History→基础专家→Observation→Hypothesis→Evidence→CounterEvidence→Critic→Confidence→Decision→Uncertainty→Gating→Counterfactual→保存Prediction→Reveal→Reflection→ReasoningMemory→下一期。

开发顺序与运行顺序不同：P14先实现Gating的纯排序接口，P15再实现调用它之前的Uncertainty约束。Critic审查的是尚未执行的ActionProposal；Proposal由版本化假设动作映射产生。其排名影响可通过纯Gating预览检查，不能先应用权重再寻求Critic许可。

| 阶段/模块 | 输入 | 输出与职责 |
|---|---|---|
| P5 ExperimentRegistry + ExpertRegistry | 实验版本与专家算法、依赖、资格、有效时间 | 固定RunIdentity及版本化注册表；专家默认禁用、不合格，人工注册后审计 |
| P6 Immutable PredictionSnapshot | Eligible Expert Pool中该期所有合法、开奖前冻结快照 | 保存Eligible/Available/Included/Missing/Excluded及理由、哈希；拒绝覆盖 |
| P7 MacroObservationEngine | PrefixContext | MacroObservationSnapshot，只有可验证事实和缺失标记 |
| P8 MacroHypothesisEngine | 观察、历史记忆 | 7类假设及替代解释集合，包含随机波动与无变化 |
| P9 MacroEvidenceEngine | 观察、假设、前缀 | SupportingEvidence及完成状态、有效样本数 |
| P10 MacroCounterEvidenceEngine | 同上及支持证据 | 主动反证检索记录；无反证不等于未检查 |
| P11 MacroReasoningCritic | 证据、反证、替代解释、待定动作 | PASS/CAUTION/REJECT、逐项理由、幅度上限 |
| P12 MacroConfidenceEngine | 上述信息和冻结校准统计 | 0～1置信度及各组成项，不用近期命中率替代 |
| P13 MacroDecisionEngine | 候选、证据、Critic、Confidence、Proposal | APPLY/HOLD/REJECT及Before/Proposed/Applied |
| P14 MacroGatingModel | 同期专家完整排名、已批准权重 | 确定性完整12生肖排名；不能修改专家评分 |
| P15 MacroUncertaintyController | Decision | 最终幅度/边界约束；只能缩小或取消动作 |
| P16 CounterfactualEngine | 同期专家和三组权重 | 固定Control、不调整、提议、实际执行4套排名 |
| P17 MacroReasoningAudit | 全部预测前产物 | 不可改写预测审计与关联键，拒绝不完整记录 |
| P18 MacroReflectionEngine | 已存Audit、后来可见开奖、旧记忆 | 追加Reflection；分别评价假设和动作 |
| P19 MacroReasoningMemory | 已成熟Reflection、expectedVersion | 新记忆版本；保存假设/证据/反证/动作/环境与校准可靠度 |
| P20 MacroWalkForwardEvaluator | 冻结参数、切分、时间序列 | 逐期执行完整推理流程与指标 |
| P21 300/600/1000验证 | 可用历史、P20 | 同期集合比较；不足数量报告实际数量，不编造旧快照 |
| P22 Live Shadow | 当日真实前缀 | 单独日更、审计、开奖反馈；仍不能决定正式Top6 |
| P23 ModelExplanationService | 保存的审计与反思ID | 结构化事实解释、引用证据，无决策写权限 |
| P24 Model Assistant | 用户问题、只读解释服务 | 查询假设、反证、HOLD原因、错误和弱点 |
| P25 实验中心及问模型UI | 独立审计与指标 | 分离展示实验与正式成绩，提示缺失/未成熟信息 |

## Observation的定义与可用性

专家来源于版本化ExpertRegistry。MacroObservationEngine读取TargetIssue预测时刻符合资格且具有合法冻结快照的动态N专家池；基础专家、元专家和已注册实验专家均可成为候选。保留模型家族、父依赖、共享输入与快照来源；缺失为Missing，不允许其他模型补位。10/20/50/100窗口仅统计N之前已揭晓且预测来源合格的记录；每项保存Window、EffectiveSamples和SourceIssueRange。无数据为null，不是0分。资格与依赖规则以[动态专家池补丁](Macro-Dynamic-Expert-Pool设计补丁.md)为准。

至少记录：各专家Top3/Top6/MRR/MeanRank；两两Spearman与Top3/Top6交集比例；冷热、遗漏结构；趋势兑现率；Immediate/Gap1/Gap2重复率；生肖频率集中度；近期分布相对100期的差异；各专家Rescue/Harm和信号有效性。

定义约定：Immediate比较Z(t)=Z(t-1)；Gap1比较Z(t)=Z(t-2)，且中间一期不同；Gap2比较Z(t)=Z(t-3)，且中间两期均不同。只使用已揭晓的t<N。集中度用sum(p_z²)，分布变化用Jensen–Shannon距离，所有窗口边界固定。Rescue/Harm以相同预测期、预注册Control和Top6为主口径，机会数与比率同时保存。

冷热标签不是生肖出号概率：ColdReturn表示此前已冻结冷组在后来已开奖样本中兑现；DeepCold、RecentlyCooling、PreviouslyActiveThenCold、LongTermRare分别依赖遗漏分位、活跃度下降、此前活跃后转冷、长期频率。RecentHot、PersistentHot、RepeatHot、CoolingFromHot、ShortBurst分别反映短期频率、跨窗持续、重复、回落和短促集中。阈值须P7预注册并仅在训练区间确定，可多标签；没有足够基准则unknown。遗漏长不能直接推出概率上升。

趋势/因子有效性需要原始贡献或可验证冻结快照；缺失贡献时不得从排名虚构反事实。P7记录missing，P9/P10降低可用样本，Decision可HOLD。

## 假设、支持与主动反证

V1仅允许ColdReturnIncreasing、HotPersistenceIncreasing、TrendSignalDegrading、RepeatRegimeIncreasing、ModelPerformanceShift、RandomFluctuation、NoMeaningfulChange。原文NoMeaningfulRegimeChange归一为NoMeaningfulChange，不另建第8类。

每次至少比较两个解释，随机波动和无明显变化永远作为候选；它们可以被选中并支持HOLD。多个假设可同时成立，CurrentProbability不要求合计1。每个假设保存先验、更新概率、状态、证据ID、创建期、可反驳规则版本及预定评价期限。

证据保存SignalName、Window、Value、BaselineValue、EffectSize、Reliability、Direction、来源区间。额外记录EffectiveSamples和DependencyGroup：10/20/50嵌套窗口不算三份独立证据。主动反证检查包括长期不支持、样本稀少、净Rescue减Harm接近0、替代解释同样强及随机波动解释。EvidenceSearch.Completed和CheckedSignalIds必须保存；Items为空只有Completed=true且无关键缺失时才可表示“已检索未找到”，不能默认当作反证弱。

建议P8～P12验证的评分为prior log-odds + 去重支持强度 - 反证强度，再乘有效样本置信项及历史可靠度；概率须经训练/验证区间校准。具体系数不在本轮实现或假定已经有效，不简单用命中次数/总数。缺少历史可靠度时用预注册保守先验，不能给满分。

## Critic、置信度与HOLD

Critic必须检查：样本量、只看短窗、追单次连胜、同强度反证、100期冲突、过大动作、相似历史环境数、随机解释、实际排名影响、未来泄漏。每项结果进入审计。泄漏、版本错配、必要证据缺失等为硬拒绝，不允许后续置信度推翻。

ActionMagnitude统一为0.5×sum(abs(w_proposed-w_before))，即转移的总权重质量。CAUTION最多0.01（1个百分点），且单专家变化也不能超过0.01；原例+1.2%与此冲突，V1按1个百分点硬上限处理。PASS的上限由预注册参数与Confidence共同限制；Uncertainty只能收紧。权重必须有限、非负、和为1。Critic=REJECT时Applied=Before、Decision=REJECT。证据不足或动作不改变有效排序可HOLD，Applied=Before、ActionMagnitude=0。每次HOLD也保存完整检查及原因。

Confidence综合支持/反证强度、有效样本、已成熟假设可靠度、环境相似度、专家一致性、Critic、校准误差。专家同源性需要扣除，不能把相关专家的一致意见当独立证据。P12校准前只能称“评分”，不能把0.8宣传为80%正确率。

## 反事实、反思与不可改写审计

固定BaseRanking用于长期基线比较；NoActionRanking用本期Before权重；ProposedRanking保留Critic/Uncertainty拦截前的提议；AppliedRanking用执行权重。4套均来自同一份目标期输入，在开奖前冻结。仅存Applied无法评价Critic拦住了什么，因此契约特意增加Proposed和NoAction。

动作归因比较NoAction与Applied：Top6未中→中为RESCUE，中→未中为HARM；双方均中且排名变化为NEUTRAL_POSITIVE；双方均未中且排名变化为NEUTRAL_NEGATIVE；真实排名完全相同为NO_IMPACT。另存排名差以区分中/未中内部的改善和恶化。DecisionCorrect以预注册排名效用比较：更好Correct、更差Incorrect、相同Indeterminate。不得把假设正确性从Top6命中直接推导出来。

每种Hypothesis在预测时冻结验证条件和期限。环境趋势假设可能需要未来若干期才成熟；尚未到期Pending，缺数据Indeterminate，不能将它们当错误或成功。单次ColdReturn不能证明ColdReturnIncreasing。Reflection只在结果可用后追加，可以分“当前动作评价”和“以后假设成熟评价”，以不同ReflectionId去重。学习仅影响以后可用记忆；评估N时只能读取N以前已经成熟并可用的反思。

MacroReasoningAudit拆成不可变PredictionAudit和append-only Reflection；ActualZodiac、OutcomeType、HypothesisCorrect、DecisionCorrect、Rescue/Harm等属于Reflection，不能在旧Audit上UPDATE。查询时通过AuditHash拼接成用户要求的完整视图。唯一键建议(ExperimentId, Issue, AuditKind/ReflectionId)；重复完全相同幂等，内容冲突拒绝。事务中校验ExpectedMemoryVersion并同时写学习收据与新记忆。失败保留错误记录，不重置或覆盖历史。

ReasoningMemory按假设、证据、反证、动作、专家环境分别保存Triggered/Matured/Correct/Rescue/Harm及可靠度估计版本；置信分桶记录0～0.2、0.2～0.4、0.4～0.6、0.6～0.8、0.8～1.0（最后闭区间）。WeaknessSummary基于成熟样本数及区间，样本少时显示不足。衰减率如采用也必须预注册，迟到结果不得反向修改之前的置信度。

## 回测、指标与LLM边界

P20逐期Observe→Hypotheses→支持→反证→Critic→Confidence→Decide→约束→Predict并冻结反事实→Reveal→Reflect→UpdateMemory。训练/验证/最终留出切分冻结；禁止完整历史归一化、事后参数选择、用当前Memory回算旧期。来源时间与结果AvailableAt统一带时区；不仅检查Issue<N，也检查AsOf时确实可见。

指标除Top3/Top6/MRR/Rescue/Harm外增加HypothesisAccuracy（成熟且可判定假设）、DecisionAccuracy（效用可比较动作）、Hold/Apply/RejectRate（全决策分母）、HighConfidenceAccuracy（≥0.8）、LowConfidenceAccuracy（<0.6）、Over/UnderconfidenceRate。后两项V1按成熟校准桶计算：平均置信度-实际正确率>0.1为过度、<-0.1为不足，同时报告桶样本量和置信区间；阈值必须预注册。不能把缺少样本的桶解释为可靠。

CriticPreventedHarm和CriticMissedRescue比较被拦截的ProposedRanking与NoActionRanking，并以被拦截次数为分母；CAUTION仅缩小幅度也保留原Proposal评估。不得以Applied=Before的相同结果宣称Critic防止了伤害。Critic历史评价只能更新下一版本参数，更新本身另走实验验证，不能放松泄漏与不可改写等硬约束。

LLM只能通过IModelExplanationService读取已存审计、反证、HOLD和反思信息；没有Rank、写权重、写记忆或调用正式预测接口。无法从审计确认的原因回答“无记录/不足”，不能现场编造。模型助手可回答当前假设、最强假设、反证、替代解释、拒绝原因、错误判断、信号弱点和过度自信，但不能凭语言输出决定生肖排名。

## 本轮交付与停止点

MacroReasoningContracts.cs为不可变record、enum及接口声明，使用ImmutableArray/ImmutableDictionary。没有Engine实现、数据库实现、DI注册、生产调用或LLM集成。本轮测试只检查契约结构和不可变容器；12项行为验收属于后续测试骨架，不报告为已通过。

P5开始前需核对运行中独立学习实现与既有审计的适配，使用适配器继承现有重复保护和版本事务，不推倒重写P0～P4。P21与P22通过且人工确认以后才讨论生产接入。
