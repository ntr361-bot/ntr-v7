# P4 Authoritative Acceptance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为V7独立学习闭环建立唯一、不可误认的P4可执行验收入口和防误接守卫。

**Architecture:** `IndependentLearningTests`继续承担真实行为验收；新增轻量结构守卫验证项目排除旧草案及正式源代码零调用。命令行入口显式区分权威P4与废弃别名，不改变任何预测或学习实现。

**Tech Stack:** .NET 10、C#、现有控制台SmokeTests、MSBuild项目文件。

## Global Constraints

- 不重新启用`OnlineLearningPipeline`。
- 不改变正式V7、两个旧自动学习模型或Top3/Top6。
- 不改写17期历史训练实验。
- 所有测试使用临时隔离数据。
- 不进入P5，不发布云端。

---

### Task 1: P4结构守卫与权威入口

**Files:**
- Create: `Tests/P4AcceptanceTests.cs`
- Modify: `Tests/Program.cs`
- Test: `Tests/P4AcceptanceTests.cs`

**Interfaces:**
- Consumes: `IndependentLearningTests.Run()`。
- Produces: `P4AcceptanceTests.Run()`和命令参数`--p4-acceptance`。

- [ ] **Step 1: 写失败测试**

在`P4AcceptanceTests.Run()`中读取两个项目文件及正式源文件，断言旧草案分别被排除、正式源码无`OnlineLearningPipeline.`调用，并运行`IndependentLearningTests.Run()`。在`Program.cs`尚无入口时，命令应无法输出P4验收标记。

- [ ] **Step 2: 验证失败**

运行：`dotnet run --project Tests/六合分析软件.SmokeTests.csproj --no-restore -- --p4-acceptance`

预期：没有`P4 ACCEPTANCE PASS`，证明入口缺失。

- [ ] **Step 3: 最小实现**

将`--p4-acceptance`路由到`P4AcceptanceTests.Run()`；把`--learning-pipeline-smoke`路由到明确错误信息和非零退出码；保留`--independent-learning-smoke`兼容入口。

- [ ] **Step 4: 验证通过**

再次运行P4命令，预期43项行为检查、结构守卫及`P4 ACCEPTANCE PASS`，退出码0；运行旧别名，预期非零且提示使用新入口。

### Task 2: 文档对齐与回归验证

**Files:**
- Modify: `docs/P0-P4-learning-plan.md`
- Modify: `docs/independent-learning-acceptance.md`

**Interfaces:**
- Consumes: Task 1验收命令。
- Produces: P4权威实现、旧草案状态和命令说明。

- [ ] **Step 1: 更新文档**

明确P4权威实现为`v7-independent-learning-v1`；旧管线与旧测试不参与编译或验收；记录新命令及不修改正式预测的边界。

- [ ] **Step 2: 运行专项和完整回归**

运行P4验收、完整SmokeTests、Release无还原构建。全部必须退出0。

- [ ] **Step 3: 检查工作区范围**

查看差异，确认只包含入口、守卫、文档和本计划，不包含模型算法、权重、数据库或17期实验数据变化。
