# V6.5 错因学习与旁路实验设计

## 目标

建立与正式 V6.5 预测链完全隔离的可追溯、可归因、可复现研究设施。正式的 50、100、全部历史和 AutoLearning 只作为不可变对照；任何实验结果均不得写回 `PredictionHistory` 或正式 `ModelMemory`。

## 已冻结的边界

- 不修改正式基础模型评分公式、固定权重、排序或正式 AutoLearning 学习逻辑。
- 不修改、回填、覆盖或重算既有正式预测和历史开奖数据。
- `PredictionTrace` 只从本设计上线后的新预测开始保存真实运行快照；不对旧期伪造“当时快照”。
- 实验模型使用独立表、独立版本号、独立参数快照和独立运行标识；默认不影响正式 Top3/Top6。
- 所有重放和回测在目标期 N 之前建立只读历史前缀，预测后才读取 N 的标签。

## 架构选择

选择“独立 SQLite 研究表 + 旁路服务”。不向 `PredictionHistory` 新增研究字段，也不只写松散 JSON 文件。

```text
正式生成（不变） ─┬─ PredictionTraceCapture ──> PredictionTrace / PredictionTraceModel
                  │                                 └─ AutoLearningTrace
                  └─ 正式 PredictionHistory（不改）

开奖验证（不变） ─── ErrorAttributionObserver ──> ErrorAttribution / FactorCounterfactual
                                               └─ ErrorAttributionStatistics

历史只读前缀 ─────── CandidateRaceService ─────> ExperimentRun / ExperimentPrediction / ExperimentMetric
```

## 数据契约

### PredictionTrace

每个新目标期一行，键为 `(Issue, TraceSchemaVersion, CaptureKind)`，其中 `CaptureKind=Live`。保存生成时间、历史截止期、样本数、代码标识、正式模型版本、状态、不可变 `PayloadJson`、SHA-256 内容哈希和建立时间。重复写入同一键仅验证 hash 一致；不覆盖。

`PayloadJson` 包含三个基础模型。每个生肖保存：名次、总分、`F/T/O/H/P/C/B` 原始值、对应贡献（原始值×该模型该因子权重，B 为直接加分）、使用的权重及总分公式。还保存 AutoLearning 的三套基础名次、归一化值、Rule/model_consensus、当时四权重、元系数、logit、softmax、完整排序。

### 开奖后观察表

`ErrorAttribution` 以 `(Issue, TraceId, Tag)` 唯一：标签、实际生肖、证据 JSON、计算版本、时间。标签允许并存。

`FactorCounterfactual` 以 `(Issue, TraceId, ModelKey, Factor)` 唯一：原始名次、移除因子后名次、`RankImpact = originalRank - rankWithoutFactor`。正值表示该因子移除后实际生肖更靠前，故它是该次的负向压力；负值表示该因子有帮助。这是关联归因，不作为因果声明。

### 实验表

`ExperimentRun` 记录代码/参数/数据区间/训练验证留出切分和创建时间。`ExperimentPrediction` 记录每个运行、模型、期号的完整排序和解释。`ExperimentMetric` 记录统一指标与分段（训练、验证、最终留出）。正式表永不被这些服务写入。

## 可重复的观察规则（v1）

- **全模型同向误杀**：实际生肖在 50、100、全部历史均排名大于 6。
- **边界错误**：Auto 或任一指定观察目标的实际排名为 7 或 8；证据保留各模型名次。
- **严重方向错误**：三个基础模型中至少两个将实际生肖排在 10–12 名。
- **模型分歧但 Auto 选择错误**：至少一基础模型实际名次不大于 6，而 Auto 名次大于 6，且三基础名次并不完全相同。
- **一致性陷阱**：至少两个基础模型把同一错误生肖置入 Top3，同时其 `model_consensus >= .5`；实际生肖 Auto 名次大于 6。证据须列出错误候选及一致性。
- **因子疑似误杀**：对 F/T/O/H/P/B 的反事实移除使实际生肖由 Top6 外进入 Top6，或名次改善至少 2。标签为 `FactorSuppression:<factor>`。

所有规则完全从已保存 Trace 计算；未找到 Live Trace 的旧期只报告“不可归因”，不重算。

## 赛马场与泄漏防护

候选模型须声明 `Predict(prefix, issue)` 并通过前缀隔离检查。每一期严格执行：`prefix=<N` → 预测并持久化实验结果 → 读取 N 实际生肖 → 仅更新该实验运行自己的状态。

统一比较 A（原正式 Auto）、B（Rule 一致性源权重上限 20/30/40%）、C（A+残差）、D（B+残差）、E（三基础平均排名）、F（最佳单基础）和 G（可复现随机基准）。训练、验证、最终留出按时间切分；留出集从不参与选择参数。候选旧模型只有通过前缀隔离适配器后才纳入。

ResidualCorrectionModel 仅输出独立排序：修正比例分别为 5%、10%、15%，并使用每生肖标准化基础分的有界修正；修正不能让生肖跨越超过两名，且所有开关默认 false。它不读取目标期标签，不写正式内存。

指标：Top1、Top3、Top6、MRR、平均/中位名次、最大连续 Top3/Top6 未中、滚动窗口最优/最差、随机 Monte Carlo 区间、模型相关性、重叠、同时失败率和 `RescueRate`。

## 验收

1. 新期 Trace 能用独立数据重放并解释任一生肖名次；重复捕获不覆盖。
2. 开奖后 observer 只写研究表，产生可验证证据和反事实结果。
3. 无 Live Trace 的历史正式记录不被修改。
4. 赛马回放在向 prefix 人为加入未来记录时，目标期预测保持不变。
5. 实验开关默认关闭，且正式预测输出、正式历史、正式 memory 的回归快照未变化。
6. 报告明确区分训练、验证、留出和旁路实验，且不把归因相关性称为因果。
