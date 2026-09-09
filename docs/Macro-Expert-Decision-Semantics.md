# Expert Decision Semantics：P5前契约补丁

本补丁接续Dynamic Expert Pool，不实现真实Gating、COUNTER、注册器或统计计算。新字段是后续实现必须遵守的语义，不等于已验证或正在运行。正式V7/V6.5、每日工作流、17期三专家实验均不修改。

## 身份、修订与依赖

ExpertId表示稳定身份；ExpertRevisionId表示具体修订，例如Integrated-V7与integrated-v7@2026-08-19-r3。AlgorithmVersion、CodeVersion继续保存。Registration、Snapshot、Observation、Decision、CounterView、统计、依赖边、Baseline和Audit均携带修订身份。按ExpertId聚合只能标为跨修订汇总并列出成员/样本；修订成绩单不得混用。相同预测中的ExpertId只绑定一个明确Revision，双版本对照使用独立实验组，不能重复投票。

MetaDependencyType区分SharedHistory、SharedFeatures、SharedRawSignals、ConsumesExpertRanking、ConsumesExpertScore、ConsumesMetaOutput和SharedLearningMemory。消费边From为消费者、To为来源；共享关系为对称关系，存储按ID规范排序并在文档中明确，不能当作父子消费边。边绑定两端Revision、EvidenceReference和Version。DependencyDepth仅对消费DAG计算：直接专家为0，派生专家为父深度最大值+1；未知为null，共享History不增加深度。DerivedFromExpertOutputs、UsesSharedMemory、UsesSameTargetBaseSnapshots逐项根据代码和实际输入登记，不能根据Meta名称推断。

EffectiveIndependentExpertCount仅供Diagnostics、Confidence、Audit及解释，不得直接输入权重公式。不能用“有效数2.4”自动除权重。高度相关不代表无信息价值，后续需把同源共识的CommonSuccess与CommonFailure一起学习，再实验评估依赖惩罚。

## 使用方式与明确反向视图

ExpertActionMode为FOLLOW、IGNORE、COUNTER；默认枚举值IGNORE。FOLLOW使用原排序；IGNORE保留观察但AppliedWeight=0；COUNTER使用ExpertCounterView。所有权重（包括RawProposedWeight）均要求有限且非负，SignedReliability另行表达潜在方向，不复用权重字段。

反向视图绑定ExpertId、Revision、TargetIssue、原始SnapshotHash、OriginalRanking、CounterRanking、TransformVersion、视图哈希及验证证据ID。变换定义full12-rank-reversal-v1：CounterRank=13-OriginalRank。仅对12个合法生肖无重无漏的完整排名定义，缺失排名拒绝，原快照不可覆盖。本轮只保存公式文本与版本，未实现反序函数或生成任何CounterView。

同一专家不能FOLLOW与COUNTER同时计票。IMacroGatingModel新增Decisions与CounterViews参数，动态非负权重字典仍以ExpertId为键，修订在冻结Pool中绑定。FOLLOW必须匹配原视图，COUNTER匹配合格派生视图，IGNORE权重为0；剩余权重合计1，全部IGNORE时HOLD/无可用预测并按预注册控制策略处理，不能除以0。

ExpertDecision保存全部请求字段：身份/修订、Mode、Global/Recent/Context/CounterReliability、Dependency/RedundancyPenalty、DiversityValue、RawProposed/Proposed/AppliedWeight、Confidence、ReasonCodes，并补充EvidenceReferences。当前只声明，没有评分公式。Mode切换也是动作，不能因权重数值不变绕过Critic/Confidence/Counter准入检查；P13～P16必须保存切换前模式及其NoAction视图，单纯权重L1幅度不能描述排序翻转。

## COUNTER严格准入

PotentialNegativeExpert依次经过BACKTEST→HOLDOUT→LIVE_SHADOW→ValidatedNegativeExpert。验证绑定Revision、变换版本、可用时间和证据哈希；修改算法后旧认证不可自动继承。足够样本、300/600/1000方向一致、原MeanRank显著差于随机6.5、反向MeanRank/MRR/Top3改善、独立留出与Shadow成立、CounterRescue>CounterHarm必须全部验证。预注册显著性、样本量、窗口一致性和多重比较规则；数据不足只能FOLLOW/IGNORE。

CounterValidationEvidence保留原版/反向各窗口指标、Holdout及Shadow报告、Rescue/Harm、阶段及阈值政策版本。阶段枚举或负SignedReliability本身不能批准COUNTER；未检查/缺失条件视为未通过。SignedReliability预留[-1,1]（null表示不足）：正向、接近0无明显信息、负向仅表示潜在反向信息。ExpertState为Positive/Neutral/Conditional/PotentialNegative/ValidatedNegativeExpert，不按Good/Bad筛掉专家。

OriginalExpert与CounterExpert分别报告Top3、Top6、MRR、MeanRank、MedianRank、MaxTop3Miss和MaxTop6Miss。完整反序下Top6与Bottom6互补，Top6低本身不证明COUNTER有预测价值；MeanRank反转也有代数关系，必须用预注册随机基准及独立样本检验，不把机械改善当泛化证据。

## 边际贡献与共识

MarginalRescue是同一期Control Ensemble不含e且未中、Candidate Ensemble含e且命中；MarginalHarm为反过来；MarginalNetImpact=Rescue-Harm。必须冻结两组的成员、Revision、Mode、权重归一化、对照策略和参数，只改变预注册的加入e操作，不能额外重新寻优其他专家。保存CommonSamples与两组ID、Split及历史模式。缺任一排名/实际结果时该期不可比较。

UniqueRescue继续表示单专家命中且主要其他专家均未中；UniqueRescue != MarginalNetImpact。展示Rescue必须同时展示Harm、净影响和共同样本数，不能凭UniqueRescue单独宣称组合价值。

预测前只存ConsensusRisk、DependencyGroupConsensus、HistoricalConsensusReliability，后者只使用已揭晓前缀。开奖后Reflection追加CommonFailure及CommonSuccess事件，不能回写PredictionAudit。CommonSuccess为预先冻结依赖组的全部合格成员命中Top6，CommonFailure为全部未中；混合结果另算在Evaluated分母，不挤进二者。ConsensusReliability的估计版本、环境、评估样本/成功/失败数一并记录，禁止把它直接等同于1-CommonFailureRate。

## 历史模式与基准

HistoricalAvailability只使用当时真实可用注册/Revision/快照，回答“当时软件如果增加Macro”。CausalReconstruction允许今天算法按前缀重建，回答“今天算法若在当年”；二者必须独立ExperimentId、统计和报告。

重建保存Reconstructed=true、SimulatedAsOf、ReconstructedAt、ReconstructionCodeVersion、HistoryCutoff、MemoryRebuiltFromScratch、TrainingPrefixHash。模拟AsOf与真实生成时间不得混淆；Live不得携带假冒旧生成时间，重建必须重新建立合法前缀记忆。本轮仅声明，未重跑历史。

P21基准：BestSingleExpert、EqualWeightEnsemble、BestStaticBlend、ThreeExpertLearning、FourExpertLearning、NExpertLearning、DependencyAwareNExpert、MacroReasoning，以及Counter专项OriginalExpert/CounterExpert。BestSingle选择与BestStaticBlend权重仅在Training确定；Validation/Holdout固定，不能拿测试结果反写先验。缺失专家处理和共同预测期也须预注册。Macro不能稳定超越静态融合时应报告复杂性尚无证据价值。

## 审计、假设范围与后续接口

ExpertPoolDecisionAudit加入PredictionAudit：IncludedExperts、ExpertRevisionIds、ExpertModes、Before/Proposed/AppliedWeights、逐专家Decision、DependencyGroups、MetaDependencyEdges、EffectiveIndependentExpertCount、CounterViewsUsed、ExcludedCounterCandidates与ExclusionReasons。所有字段一致性由未来P17验证：不能给出相互冲突的重复权重或Mode。

原7类Macro V1假设不变；专家扩展优先仅预留ExpertPerformanceShift和CommonExpertFailure。ExpertWrapperDegradation、ExpertFamilyBias、ExpertDiversityOpportunity只保留enum，不进入V1候选或证据累计。

Model Assistant只查询审计与已完成统计，支持询问为何FOLLOW/IGNORE、为何潜在负专家、为何未COUNTER、反向成绩、边际Rescue/Harm、同源关系、共识可靠性、低命中专家为何保留。不存在证据时明确答无记录，不能现场解释为已运行规则。

P5完成修订注册与依赖语义；P6冻结重建来源和Revision；P7加入边际与共识观察契约；P13保存模式提议；P14～P16以后实现视图选择与反事实；P17完整审计；P18开奖后共识反馈；P20/P21隔离历史模式和静态基准。Counter验证未完成之前任何真实模式只能FOLLOW或IGNORE。

## P5前统计约束补充

本节收紧前文的验证语义，不改变架构。字段与测试骨架不是已实现的准入检查。

1. 对 `full12-rank-reversal-v1`，Counter Top6 等于 Original Bottom6，与 Original Top6 天然互补。Counter Top6 > 50% 不能单独证明 COUNTER 有效；主要依据独立未见样本上的 Top3、MRR、实际名次相对随机基线、指定组合中的 MarginalNetImpact 和 Live Shadow，不重复计算机械变换为独立证据。
2. 单期 `Counter ActualRank = 13 - Original ActualRank`；相同样本集合上 `Counter MeanRank = 13 - Original MeanRank`。反转后均值改善是代数结果，不能作为有效性证据。应检验未见样本上 Original ActualRank 是否稳定显著偏离随机中心 6.5（反向候选关注高于该中心）。`CounterInferencePlan` 预留 BlockBootstrap / RollingBlockPermutation，记录区块政策、长度、次数、种子、零假设、统计量、显著性与多重比较规则、稳定性判据、评估区间及预注册哈希。不能以简单 IID 检验代替序列依赖处理；区块与零假设如何成立仍须后续验证，本轮不计算。
3. 派生视图与认证证据同时绑定 `CounterTransformId` / `TransformVersion`。完整倒序只是 V1 候选；现有常量是候选声明，不是未来方法白名单。其他变换需要独立版本、预注册及验证，不能继承完整倒序的认证或互补性质。
4. `ExpertModeSelection` 保存 TargetIssue、HistoryCutoff、AsOf、SelectedAt、政策版本、证据哈希及所选变换。P20 每期必须用 N 之前且当时已可见的数据选择 Mode/变换，再冻结预测；开奖后只评价，不得事后选 FOLLOW 或 COUNTER。模式选择本身属于 Walk-Forward 策略，不只是冻结权重。
5. `StaticBlendSearchRegistration` 记录训练区间、搜索空间、粒度、正则、目标、搜索预算、缺失处理及预注册时间/哈希；BestStaticBlend 必须提供，其他基准可为空。搜索设计在训练搜索前注册；训练选定的权重和所有政策在 Validation/Holdout 前完全冻结。不能根据验证或留出成绩扩展搜索空间、改粒度/正则、重选训练区间后仍称为同一个未见检验。
6. MarginalRescue/Harm/NetImpact 必须绑定 ControlEnsembleId 与 CandidateEnsembleId，并保留既有修订、模式、冻结比较政策、Split 和共同样本数。它回答“此专家加入这个特定组合后如何”，不是专家的绝对价值，不能移植到另一个组合或与不同对照直接混算。

新增8条统计行为验收骨架均为 PENDING；可执行检查只验证契约形状，不证明统计方法或准入已经有效。

本轮交付为MacroExpertDecisionContracts.cs及相关契约/文档/测试骨架更新，无真实算法、无启用专家、无生产接入、无COUNTER实验、无云端发布。
