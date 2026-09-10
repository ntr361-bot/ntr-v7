# P0–P4 独立学习模型设计

## 目标

将现有 `OnlineLearningPipeline` 草稿改造成第三个、完全旁路的学习模型。它可每天生成冻结预测、在开奖后学习并保留可审计证据，但绝不修改 V6.5、V7、正式 Top6、正式 `PredictionHistory`、每日正式云端预测或既有 17 期实验。

## 明确边界

- 运行身份：`V7-IndependentLearning-P0P4`，不是 V6.5 AutoLearning，也不是 V7 AutoLearning。
- 数据库：专用 `data/experiments/p0p4-independent-learning/p0p4-learning.db`。
- 历史来源：只读正式 `history.db` 的已开奖记录；预测目标期 N 只可读取小于 N 的记录。
- 正式隔离：不得写入 `PredictionHistory`、`ModelMemory`、`V7PredictionHistoryService`、正式运行状态或任何正式模型权重。
- 云端隔离：P0–P4 的每日 Shadow 文件可在后续单独发布；本次只完成本地运行与审计，不改变云端工作流。

## 数据模型

专用数据库包含以下只追加或受事务保护的数据：

| 数据 | 用途 | 不可变规则 |
|---|---|---|
| `LearningInputSnapshot` | 预测前的完整输入、12生肖排名、截止期、MemoryVersion、输入哈希 | 同一模型/期号只能存在一个内容相同的快照；冲突拒绝 |
| `ShadowPrediction` | P0–P4 的预测 Top3/Top6、完整排名、生成时间、来源快照 | 不写正式预测历史；同一期冲突拒绝 |
| `LearningAudit` | 开奖后状态变更前后、实际生肖、名次、错误或成功原因 | 成功学习一期开一次；失败审计可保留 |
| `ModelMemory` | 专属权重、元系数、学习样本数、版本、最后学习期 | 只由专属学习事务更新 |
| `LearningReceipt` | 去重与补齐凭据 | 已成功学习的期不可再次改变 Memory |

所有表以 `ModelKey=V7-IndependentLearning-P0P4` 隔离；正式表不创建、不迁移、不写入。

## 预测与学习流程

### 预测 N

1. 读取正式历史中所有期号 `< N` 的已开奖记录。
2. 读取本模型专属 Memory；若 `LastTrainingIssue >= N`，拒绝生成。
3. 用当前正式 V6.5 基础预测构建输入，但禁止读取 N 的开奖、正式 PredictionHistory、其他学习器的 Memory。
4. 生成完整12生肖排名并创建不可变 Snapshot 与 ShadowPrediction。
5. 预测创建仅追加，不学习、不写正式表。

### 开奖后学习 N

1. 确认 N 已出现在正式历史，且 Snapshot 的截止期 `< N`。
2. 检查 Receipt；若已学习则原样返回，不改变 Memory。
3. 严格按 `LastTrainingIssue` 的下一期处理，禁止跳期。
4. 使用 N 的实际生肖更新专属 Memory，并在同一事务保存 Audit、Receipt 和 Memory。
5. 若事务失败，Memory 与成功 Receipt 均不落库，并写入失败审计。

### 缺失快照补齐

若历史某期有开奖但无 Snapshot，可使用当时历史前缀与当时专属 Memory 因果重建。重建记录必须标记 `Reconstructed=true`，不可覆盖正式历史、不可伪装成实盘 ShadowPrediction。

## P0–P4 对应交付

| 阶段 | 交付 |
|---|---|
| P0 | 专用数据目录、模型身份、隔离数据库、Schema 与状态健康检查 |
| P1 | 冻结输入快照、完整12生肖排名、截止期/哈希/MemoryVersion 验证 |
| P2 | 开奖后单期学习、事务审计、重复学习保护 |
| P3 | 断点恢复、顺序 Gap Recovery、因果重建标记与失败可重试 |
| P4 | 每日 Shadow 入口、历史回放验收、实验中心读取接口与正式链不变回归测试 |

## 错误处理

- 目标期或未来期进入输入：拒绝并留下健康状态原因。
- 缺失或重复生肖排名：拒绝快照。
- Memory 游标晚于可见开奖历史：停止学习，不自动回滚。
- Snapshot/Receipt 内容冲突：拒绝覆盖。
- 事务失败：回滚 Memory 与成功 Receipt，保存失败审计。
- 正式历史不完整：不猜测实际生肖，不学习。

## 验收测试

1. 新模型数据库与正式数据库路径不同。
2. 预测 N 只使用 N-1 及以前数据，N 的开奖进入输入时失败。
3. 每次有效预测生成完整12生肖无重无漏的冻结 Snapshot 和 ShadowPrediction。
4. 同一期重复预测和重复学习不会变更 Memory。
5. 学习前后 MemoryVersion 恰好加一，Audit 记录前后值。
6. 断点后按顺序补齐；缺失期不能跳过。
7. 重建只标记旁路记录，不能写正式 PredictionHistory。
8. 事务故障不产生部分成功状态。
9. 软件重启后读取同一专属数据库可继续学习。
10. P0–P4 前后正式 V6.5/V7 的 Top3、Top6、权重、PredictionHistory 和运行状态哈希均一致。

## 不在本次范围

- 不启用 Macro 权重调整、COUNTER 或生产专家。
- 不修改 V6.5/V7 评分、预测、权重或学习算法。
- 不写入智能账本、Cloudflare 或 GitHub Actions。
- 不把 P0–P4 成绩称为正式预测成绩。
