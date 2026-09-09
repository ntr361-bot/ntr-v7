# P6 不可变专家快照与冻结专家池设计

## 目标

P6 为 P5 注册专家和 P7 观察引擎建立中间事实层：保存开奖前形成的完整 12 生肖专家排名，审核每期动态专家池，并保证旧快照不能被新运行覆盖。P6 不预测、不学习、不调权、不接生产。

## 方案选择

采用 SQLite 追加式旁路库。相比 JSON 文件，SQLite 能用唯一键和事务直接保证并发写入时的不可覆盖；相比写入正式 `PredictionHistory`，独立旁路库不会污染正式历史。当前实现不增加远端同步或 UI。

## 快照身份与不可变性

快照唯一身份为 `SnapshotOrigin + ExpertRevisionId + TargetIssue`。同一身份、同一规范哈希的重复写入视为幂等；同一身份但内容不同一律拒绝。规范哈希覆盖专家身份、修订、目标期、历史截止、生成/可用时间、完整排名、算法与代码版本、注册表版本、来源模式、父快照哈希、输入依赖及重建证明，不信任调用方传入的旧哈希。

写入前必须确认：专家修订已经登记；算法和代码版本与注册记录一致；历史截止早于目标期；排名为 12 生肖无重无漏；LiveFrozen 快照在本次冻结时刻已经生成且可用，并且没有重建证明；CausalReconstruction 必须带完整重建证明、从头重建记忆并绑定训练前缀哈希。消费其他专家排名的修订必须引用同目标期、已保存且版本匹配的父快照哈希。

## 动态专家池审核

`ExpertPoolResolver` 以注册表快照、目标期、AsOf、候选快照和历史模式为输入：

- `Eligible`：在时点有效、启用、明确允许 Macro、完整排名声明和两项审计均通过的唯一 Revision。
- `Available`：该目标期实际提供过快照的专家，不表示快照合法。
- `Included`：Eligible 且有一份通过逐期完整性、版本、时间、来源和哈希审核的快照。
- `Missing`：Eligible 但没有其绑定 Revision 的快照。
- `Excluded`：未启用、不合格、审计未通过、版本冲突或快照逐期审核失败，并保存原因。

同一 ExpertId 同时存在两个有效 Revision，或同一期候选中出现冲突 Revision/冲突内容时拒绝冻结，不自动挑选。缺失专家不补位，坏快照不修补。

## 两种历史模式

`HistoricalAvailability` 只接受 `LiveFrozen`，要求 `GeneratedAt` 与 `AvailableAt` 不晚于真实 AsOf。`CausalReconstruction` 只接受明确的重建来源，使用 `SimulatedAsOf` 约束历史前缀，同时保留真实 `ReconstructedAt`，不得伪装成旧日实盘快照。两种模式使用不同池键和统计结果。

## 冻结池存储

冻结池规范哈希覆盖完整 `ExpertPoolSnapshot`。唯一身份为 `EvaluationMode + TargetIssue + AsOf + RegistryVersion`；相同内容幂等，冲突内容拒绝。P7读取的是已冻结 Pool 和与之哈希匹配的 Included 快照。

## 测试与边界

验收包括：规范哈希稳定、快照冲突拒绝、未知修订拒绝、算法/代码错配拒绝、11生肖拒绝、未来时间拒绝、父快照缺失拒绝、Eligible/Available/Missing/Excluded分类、双Revision冲突、缺失不补位、两种历史模式隔离、池冲突拒绝、P7可读取冻结前缀、正式V7结果不变。

当前六个 P5 候选仍保持 `EligibleForMacro=false`、`Enabled=false`。P6 不修改正式 V7/V6.5、17期实验、每日工作流、云端或正式 Top6。
