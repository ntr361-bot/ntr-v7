# P4 权威验收入口设计

## 目标

P4以已经隔离实现的`v7-independent-learning-v1`为唯一权威学习闭环。完善工作只解决验收入口、阶段身份和防误接保护，不重新启用旧`OnlineLearningPipeline`，不改变正式V7、V6.5自动学习、V7自动学习、正式Top3/Top6或17期历史实验。

## 权威实现

P4运行链由`IndependentLearningModel`、`IndependentLearningDaily`、`IndependentLearningHistoricalTraining`和`IndependentLearningHistoryForm`组成。旧`OnlineLearningPipeline.cs`及`Tests/LearningPipelineTests.cs`是被明确排除编译的历史草案，不作为P4完成证据。

## 修改范围

1. 新增明确的`--p4-acceptance`测试入口，运行独立学习的完整验收集合。
2. 保留`--independent-learning-smoke`兼容入口。
3. 将误导性的`--learning-pipeline-smoke`改为明确拒绝，并提示使用`--p4-acceptance`，避免旧草案被误认成已验证实现。
4. 增加P4结构守卫：主项目必须排除`OnlineLearningPipeline.cs`，测试项目必须排除`LearningPipelineTests.cs`，正式源代码不得调用旧管线。
5. 增加正式预测不变守卫，验证P4验收使用隔离临时目录，并保持旧模型记忆与正式历史字节不变。
6. 更新P0～P4文档，明确权威实现和旧草案状态。

## 验收标准

- `--p4-acceptance`执行独立学习全部行为检查并返回0。
- 旧别名不能静默运行另一套测试。
- 旧草案仍不参与主程序和测试程序集。
- 每日旁路失败不能中断正式预测。
- 正式历史、两个旧模型记忆和17期历史实验不被修改。
- 完整回归测试及Release构建通过。

## 非目标

不恢复旧管线，不迁移正式数据库，不重训历史，不改变学习公式，不进入P5，不推送或发布云端。
