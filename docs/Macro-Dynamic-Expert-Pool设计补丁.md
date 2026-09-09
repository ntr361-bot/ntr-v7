# Dynamic Expert Pool：契约与架构补丁

进一步语义以[Expert Decision Semantics](Macro-Expert-Decision-Semantics.md)为准：身份与Revision分离，明确FOLLOW/IGNORE/COUNTER、边际贡献和两种历史模式；有效专家数仅诊断，不直接控制权重。

本补丁替换固定4专家假定。当前只增加record/enum/interface与测试骨架，没有注册器、依赖算法、Gating实现或生产接线。既有v7-independent-learning-v1及17期历史训练作为3-expert baseline原样保留。未来比较3专家、4专家、全专家、依赖去重、Reasoning时必须固定同一期样本集合和各自实验ID。

## 真实代码依赖登记草案

以下为当前源码核查结果，不是已启用的注册记录。AlgorithmVersion必须由实际算法及参数摘要确定，CodeVersion绑定确切提交及脏工作区源码摘要；不能只写显示名称V7或推测旧期版本。所有候选本轮EligibleForMacro=false，Enabled=false，审计状态Unknown。

| ExpertId | 家族/类型 | ParentExpertIds | InputDependencyIds与代码证据 |
|---|---|---|---|
| V65-50 | V65/Base | 空 | History前缀、V65ExperimentPipeline/V65RuleScoringEngine、50期参数 |
| V65-100 | V65/Base | 空 | 同上，100期参数 |
| V65-All | V65/Base | 空 | 同上，动态全历史参数 |
| V65-Auto | V65-Meta/Meta | V65-50、V65-100、V65-All、Integrated-V7（当前SaveAutoLearning路径） | V7PredictionHistoryService.SaveAutoLearning→BuildFromBasePredictions→MetaPredictionEngine；独立Auto记忆/系数/初训数据 |
| Integrated-V7 | V7/Base（直接规则专家） | 空 | V7Engine.Predict→FeatureEngine.BuildFeatures(history,0)→EngineScoring.Build；没有聚合其他专家排名 |
| V7-Auto | V7-Meta/Meta | 空（当前未消费已注册专家的排名） | SaveIntelligentAutoLearning→HistoricalMetaSnapshotBuilder→FeatureEngine、MarketStateEngine及手写AI/ML/State/V7分数→MetaPredictionEngine；IntelligentHistory记忆 |

V65-Auto中的Integrated-V7是同历史前缀重新调用V7Engine得到的排名，不是直接读取同一期存档；必须保存本次实际父计算的输入/版本/结果哈希，不能误指向事后保存的另一份V7快照。BuildFromBasePredictions允许不传v7Ranking，此时采用默认rank7；这种调用路径的实际依赖与SaveAutoLearning不同，应按版本/路径明确标注，不能虚构已消费V7。

V7-Auto输入字典中名为V7的手写分数不是Integrated-V7输出，不能据名称登记父子关系。它与Integrated-V7共享FeatureEngine与History，属于共享输入关系。若以后调用路径改变，应新建注册版本。Memory还需审计实际训练来源与UsedMemoryVersion，这次只核实直接推理依赖，未宣称旧学习记忆已完成泄漏审计。

Integrated-V7名称虽是“整合”，当前源码为直接特征规则评分，先过滤ShortForbidden，导致某些期只有11生肖。此类快照Excluded:IncompleteRanking；不能补零、补第12名或借用其他模型排序。2026244先前档案就存在此风险，因此用户的rank12对rank1示例可用于解释家族共错，但只有合法完整快照才能实际参与Macro比较。

## 注册与逐期专家池

ExpertRegistration保存ExpertId、DisplayName、ModelFamily、ModelType、AlgorithmVersion、CodeVersion、IsBaseExpert/IsMetaExpert/IsExperimental、ParentExpertIds、InputDependencyIds、EligibleForMacro、Enabled、PredictionSnapshotType、HasFullRanking12、LeakageAuditStatus、SnapshotIntegrityStatus，并增加RegisteredAt/EffectiveFrom和依赖证据引用。ExpertId稳定，版本组成唯一注册修订；修订追加，不覆盖历史。

注册表通过不代表每一期快照合格。每一期还需12生肖无重无漏、同TargetIssue、Cutoff<N、生成时间与AvailableAt均早于开奖且不晚于AsOf、算法和注册版本匹配、父快照和输入来源可追踪、学习记忆无未来信息。环状父依赖、未知父来源、同专家多份冲突快照均应拒绝。不能仅凭注册表HasFullRanking12=true跳过逐期检查。

- EligibleExperts：AsOf时已明确注册、启用、资格审核通过的专家。
- AvailableExperts：实际发现快照的候选专家，不代表合法。
- IncludedExperts：Eligible中存在合法快照且通过逐期审核者。
- MissingExperts：Eligible中没有快照者。
- ExcludedExperts：不具资格、禁用或有快照但不合法者，逐项保存理由。Missing与Excluded不混淆。

PredictionAudit同时冻结上述集合、注册版本、Included快照哈希、DependencyGroups及EffectiveIndependentExpertCount。PrefixContext与Observation也携带同一池摘要，防止中途切换专家。

## N专家权重及信息价值

Gating现有接口已是ImmutableDictionary<string,double>；字符串键明确为ExpertId，不是固定四维槽位。后续校验Keys恰等于IncludedExperts、所有值有限非负、总和1。空池不能产生排名；单专家可以作为退化实验，置信度不能虚增。候选池变化时Before/Proposed/Applied必须对齐同一支持集，另存入池/退出与归一化规则，不能把缺失造成的机械重分配记为推理收益。

表现差不构成资格排除理由。合格专家即使Top6仅23%也保留，学习其Global/Recent/ContextReliability、Rescue/Harm/NetImpact、MeanRank、DiversityValue、DependencyPenalty。可以由经过验证的算法降低权重，不能由开发者先删掉。起点为冻结统一先验或仅Training区间确定的先验，禁止用验证成绩手动初始化Integrated-V7=60%。

## 依赖、家族和有效证据

ExpertDependencyAnalyzer分别记录父边、共享输入和统计相关性。SharedInputRatio候选定义为版本化原子输入集合的Jaccard比例，Memory训练来源需展开；全部共享History不能单凭这一项认定完全等价。两两RankingCorrelation10/20/50/100用相同合法期完整排名的Spearman均值；Top6Overlap=交集数/6；保留共同样本数。

ResidualDiversity评估训练前缀拟合的共同成分之外的残差差异；DependencyPenalty与RedundancyPenalty为后续预注册估计器输出，本轮不编写惩罚公式或赋默认数值。专家数量不等于独立证据数。有效专家数可实验评估PSD相关矩阵的有效秩(tr C)^2/tr(C²)，完全一致接近1、不相关接近N；缺失/不同样本对不能直接组成合法矩阵，样本不足输出null。谱指标不是独立性的证明，还需结合结构依赖；EstimatorVersion与样本范围随审计保存，不预填“2.4”。

FamilyReliability覆盖V65、V7、V65-Meta、V7-Meta，允许上卷MetaLearning组；单专家一个主FamilyId，多重来源通过依赖DAG表示。家族分层FinalWeight=FamilyWeight×WithinFamilyWeight作为独立实验策略候选，暂不作为默认。先比较统一N专家、依赖惩罚和家族分层，避免重复计算同一惩罚。

## 专家Observation、UniqueRescue与CommonFailure

Observation增加每专家10/20/50/100的Top3/Top6、MRR、MeanRank、Rescue、Harm、NetImpact、RankingChangeRate、Top6UniqueContribution、DiversityContribution、FamilyId和DependencyGroup。

UniqueRescue(N,e)=专家e命中Top6且预测前冻结的“主要其他专家集合”R(N,e)全部未中。只在R非空且所有对照快照合法并已验证的共同期计算；机会数为R全部未中的期数，比率为UniqueRescue/机会数。机会数0显示null。R必须由预注册规则和N以前数据选定，不能开奖后挑选对照。缺失对照不算未中，也不能利用空集合得到必然UniqueRescue。Top6UniqueContribution可定义为e的Top6中未被R任何成员覆盖的比例（开奖前）；它不同于开奖后的UniqueRescue。

CommonFailureDetection开奖后记录某冻结依赖组内全部有效专家实际生肖排名>6的事件、各名次和其他组的救回专家。最低组规模、依赖阈值和覆盖要求提前冻结；缺失组员标记覆盖不足，不宣称全组共错。预测前只能观察分歧/同源共识风险，不能知道当前CommonFailure；Observation只能读取过去已揭晓事件。

RankingChangeRate与NetImpact必须指定冻结对照（父专家或NoAction）；没有单一父排名时不能把Integrated-V7强、V7-Auto弱直接解释成包装层破坏了它。ExpertWrapperDegradation只有实际父依赖与同输入对照成立才可检验。

## 假设扩展与Walk-Forward

原7类假设维持V1；ExpertHypothesisExtension单独预留ExpertPerformanceShift、ExpertWrapperDegradation、CommonExpertFailure、ExpertFamilyBias、ExpertDiversityOpportunity，不自动加入当前候选列表。与ModelPerformanceShift重叠时用版本化别名/替换策略避免双算支持证据。

P20每一期按AsOf重建注册资格、版本、快照与依赖，不把现有模型倒填成过去存在。LiveFrozen与CausalReconstruction分开实验：重建需仅历史前缀训练、重建自己的记忆、记录实际重建日期与模拟AsOf，证明无泄漏；不能把重建生成时间伪装成当年。无证据则Unavailable。注册表的真实RegisteredAt和实验允许重建的策略是两层信息，重建模式不能借注册策略冒充历史实盘可用性。

## 后续阶段与本轮边界

P5同时设计ExpertRegistry版本及DAG；P6实现池快照审核；P7加入专家、依赖和家族Observation；P8预留扩展假设；P14实现N专家与支持集校验；P17保存池审计；P20逐期动态重建；P21比较3/4/N/去重/Reasoning。其余P5～P25顺序不变。

本轮新增MacroExpertPoolContracts.cs，扩充MacroReasoningContracts.cs的快照版本、来源、Pool和Dependency字段，测试只做契约检查及未来验收清单。未创建真实注册记录、未启用任何候选、未运行新模型训练、未接云端或生产，正式Top6及既有三源实验不变。
