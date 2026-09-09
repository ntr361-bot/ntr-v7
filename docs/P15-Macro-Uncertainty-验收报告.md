# P15 Macro Uncertainty 验收报告

验收日期：2026-09-09

## 实现结果

P15 已实现为独立旁路安全层。它只处理 P13 已生成的不可变 MacroDecision，能够保持、缩小或取消动作，不能放大动作、反转方向、增加专家或改变 ProposedWeights。

V1 规则：低于0.60置信度HOLD；Critic REJECT强制REJECT；CAUTION基础上限1%，PASS基础上限5%，再乘当前置信评分形成更保守上限。该评分仍不是已校准概率。

## 隔离边界

- 未接正式 V7/V6.5。
- 未修改正式 Top3/Top6。
- 未修改专家模式或启用 COUNTER。
- 未写 PredictionHistory、模型记忆或数据库。
- 未修改每日工作流，未发布云端。

## 验收

- P15专项覆盖15个HOLD/REJECT、Critic否决、低置信度、CAUTION/PASS缩放、方向保持、支持集与数值完整性场景，全部通过。
- P5-P15专项全部通过。
- 完整Smoke回归退出码0。
- Release编译退出码0，0个错误，1个警告。
