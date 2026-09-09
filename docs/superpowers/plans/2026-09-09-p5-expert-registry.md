# P5 Expert Registry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现追加式版本专家注册表、类型化依赖DAG和六个源码事实候选专家目录。

**Architecture:** 新增独立SQLite注册表，不被正式预测代码调用。注册记录与依赖边事务写入、不可覆盖；消费边执行未知修订和环检测，共享关系不参与深度。冻结Pool只验证同一ExpertId绑定唯一Revision。

**Tech Stack:** .NET 10、C#、System.Data.SQLite、现有SmokeTests。

## Global Constraints

- 不修改既有Macro契约。
- 默认EligibleForMacro=false且Enabled=false。
- 不实现Gating、COUNTER、Macro推理、权重学习或生产接入。
- 不修改正式预测和17期三专家实验，不发布云端。

---

### Task 1: 追加式注册表和DAG

**Files:**
- Create: `ExpertRegistry.cs`
- Create: `Tests/ExpertRegistryTests.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Produces: `VersionedExpertRegistry.AppendRevision`、`AppendDependency`、`ReadAsOf`、`FreezePool`、`DependencyDepth`。

- [x] 先写九项失败测试：重复、追加、未知依赖、环、共享边、Depth、双Revision Pool、默认禁用、正式预测不变。
- [x] 运行`--p5-registry-smoke`确认因实现缺失而失败。
- [x] 使用SQLite事务实现最小注册与DAG逻辑。
- [x] 重跑专项测试至通过。

### Task 2: 六专家事实目录和报告

**Files:**
- Create: `CurrentExpertCatalog.cs`
- Create: `docs/P5-Expert-Registry-实际注册表与血缘.md`

**Interfaces:**
- Produces: `CurrentExpertCatalog.Seed`，只登记源码可证明字段与类型化边。

- [x] 为六个候选建立禁用注册，源码无法核实字段保存Unknown/null。
- [x] 根据实际调用登记消费边；共享History/Features单独登记。
- [x] 生成注册表及DAG报告。
- [x] 运行P5专项、完整回归和Release构建，确认正式输出守卫不变。
