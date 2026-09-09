# V7 P0–P4：在线学习闭环

后续架构修订（2026-09-08）：P0～P4保持现状；P5以后以[Macro Reasoning架构与计划](Macro-Reasoning-架构与P5-P25计划.md)为准。本补丁仅定义契约和测试骨架，未新增生产调权链。

> 2026-09-09验收澄清：本文件以下内容是旧`OnlineLearningPipeline`草案的历史计划，不是当前已编译实现。该草案及其旧测试已明确排除编译。当前P4唯一权威实现是`v7-independent-learning-v1`，验收命令为`--p4-acceptance`；详情见[独立学习入口验收报告](independent-learning-acceptance.md)。废弃参数`--learning-pipeline-smoke`会明确失败，不能再静默冒充P4验收。

范围：仅修复 V7 仓库中旧 Auto 的学习基础设施；冻结 V7 和 V6.5 三个基础评分器，保留旧记忆、正式预测和界面。不部署 Macro，不发布网站。

## P0 只读发现

- DailyPredictionAutomation.Generate → SaveAutoLearning → EnsureInitialTraining 只在 LearnedSamples=0 时预训练，已有记忆不会追赶最新开奖。
- VerifyPrediction 仅访问未验证记录；验证后但学习失败的记录不再被扫描。
- ApplyAutomaticLearningForPrediction 的 Processing 领取、Memory 保存、Learned 标记不在同一事务；Error/Processing 无重试。
- MetaPredictionEngine.Learn 早于 ApplyFeedback 的重复检查，重复调用可先修改系数。
- runtime-state 只携带预测与 Memory，审计和输入版本尚不存在。
- 工作区已有历史显示/同步修复，必须保留且不得混入本轮提交。

## 旧草案原计划（不作为完成证据）

1. P1：版本字段与底层重复保护（不改变评分/学习公式）。
2. P2：独立不可覆盖的学习输入快照、LearningAudit、事务内逐期学习、失败审计。
3. P3：生成前追赶、历史前缀硬校验、新预测 UsedMemoryVersion、运行状态携带证据、恢复失败非绿色成功。
4. P4：隔离 SQLite 模拟 N 预测→开奖→日常生成 N+1；重复、断档失败、恢复、事务失败、版本失配、未来数据、状态往返测试；验收报告。

缺失旧输入只允许使用截至目标期之前的历史和尚未学过目标期的记忆重建，另存 Reconstructed 输入，绝不覆盖正式历史。已有无审计旧 Memory 保留为迁移基线，不伪造历史审计；新增成功学习必须有唯一 (ModelKey, Issue) 审计且与 Memory 同事务提交。

验收用隔离数据目录，正式数据库不运行迁移/补学。本阶段学习修复可能使未来 Auto 使用原本漏掉的经验，但基础专家和已存 Top3/Top6 不变。
