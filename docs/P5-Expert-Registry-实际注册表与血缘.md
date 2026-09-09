# P5 Expert Registry 实际注册表与血缘

日期：2026-09-09。范围仅为P5注册表与Dependency DAG；没有Macro、Gating、COUNTER、调权或生产调用。

## 实际注册表

| ExpertId | ExpertRevisionId | Family / Type | AlgorithmVersion | Depth | Eligible / Enabled | 源码证据 |
|---|---|---|---|---:|---|---|
| V65-50 | V65-50@v65-rule-current | V65 / Base | unknown | 0 | false / false | `V65ExperimentPipeline.cs`; `AIEngine.cs` |
| V65-100 | V65-100@v65-rule-current | V65 / Base | unknown | 0 | false / false | `V65ExperimentPipeline.cs`; `AIEngine.cs` |
| V65-All | V65-All@v65-rule-current | V65 / Base | unknown | 0 | false / false | `V65ExperimentPipeline.cs`; `AIEngine.cs` |
| Integrated-V7 | Integrated-V7@f055-o045-v1 | V7 / Composite | frequency-0.55-omission-0.45 | 0 | false / false | `V7PredictionEngines.cs`的`V7Engine.Predict`及`EngineScoring.Build` |
| V65-Auto | V65-Auto@meta-current | V65-Meta / Meta | unknown | 1 | false / false | `V7PredictionHistoryService.SaveAutoLearning`; `AutoLearningSnapshotBuilder.BuildFromBasePredictions` |
| V7-Auto | V7-Auto@historical-meta-current | V7-Meta / Meta | unknown | 0 | false / false | `V7PredictionHistoryService.SaveIntelligentAutoLearning`; `HistoricalMetaSnapshotBuilder` |

`CodeVersion`由每次建立注册表的调用方显式传入并写入每条Revision；无法从源码常量确定的算法版本保留`unknown`。六条记录的LeakageAuditStatus与SnapshotIntegrityStatus均为Unknown，没有因登记而自动获得资格。

## 消费DAG（有向，消费者 → 来源）

```text
V65-Auto@meta-current
  ├─ ConsumesExpertRanking → V65-50@v65-rule-current
  ├─ ConsumesExpertRanking → V65-100@v65-rule-current
  ├─ ConsumesExpertRanking → V65-All@v65-rule-current
  └─ ConsumesExpertRanking → Integrated-V7@f055-o045-v1
```

这是当前唯一得到源码证明的专家输出消费关系。Depth只按以上消费边计算，因此V65-Auto为1，其余为0。

## 共享关系（无向，不增加Depth）

```text
V65-50 ─ SharedHistory ─ V65-100
V65-50 ─ SharedHistory ─ V65-All
V65-100 ─ SharedHistory ─ V65-All

Integrated-V7 ─ SharedHistory ─ V7-Auto
Integrated-V7 ─ SharedFeatures ─ V7-Auto
```

`Integrated-V7`直接使用`FeatureEngine.BuildFeatures`；`V7-Auto`的`HistoricalMetaSnapshotBuilder`也使用FeatureEngine，并额外使用MarketStateEngine。源码没有显示V7-Auto直接读取Integrated-V7的排名或分数，因此没有人为添加消费边。

## 存储与拒绝规则

`VersionedExpertRegistry`使用独立SQLite文件保存Revision JSON和类型化依赖边。Revision主键只允许追加；重复Revision、Revision与ExpertId冲突、未知依赖Revision、重复边和消费环均拒绝。共享边要求按RevisionId规范排序且不参与DAG深度。

冻结Pool会先验证所有Revision在AsOf时存在，再拒绝同一ExpertId双Revision。当前六个专家全部禁用，因此P5不会产生任何Included专家或预测。

## 验收边界

专项测试覆盖重复、追加、未知依赖、环、共享关系、Depth、双Revision Pool、默认禁用、真实目录血缘以及注册前后正式V7 Top6一致。P5没有修改正式模型、17期三专家实验、每日工作流或云端。
