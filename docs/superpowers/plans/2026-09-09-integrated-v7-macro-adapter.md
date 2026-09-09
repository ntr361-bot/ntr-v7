# Integrated V7 Macro Expert Adapter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 Integrated-V7 提供不影响生产行为的完整12生肖 Macro 排名和 P6 冻结快照。

**Architecture:** 独立适配器从历史前缀重算现有 V7 底层分数，分区排序正常和硬排除生肖；独立快照服务将结果封装、密封并追加到 P6。正式 `V7Engine.Predict` 与保存链保持不变。

**Tech Stack:** C# 13、.NET 10、System.Data.SQLite、现有自定义 smoke-test runner。

## Global Constraints

- 不修改正式 V7 Top6、ShortForbidden、概率、PredictionHistory、权重或生产链。
- 不启用 Macro，不开发 P15，不处理 V65-Auto/V7-Auto。
- 所有新Revision只能追加，默认 EligibleForMacro=false、Enabled=false。
- 使用 TDD，先确认专用测试因缺少功能失败。

---

### Task 1: FullRanking12 adapter

**Files:**
- Create: `IntegratedV7MacroExpertAdapter.cs`
- Create: `Tests/IntegratedV7MacroExpertAdapterTests.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Consumes: `FeatureEngine.BuildFeatures(IReadOnlyList<HistoryRecord>, int)`
- Produces: `IntegratedV7MacroExpertAdapter.Build(history)` and immutable 12-item result.

- [x] Add tests with literal fixtures covering zero, one and two hard exclusions, completeness, stable ties, determinism and formal Top6 invariance.
- [x] Run `--integrated-v7-macro-adapter-smoke` and confirm it fails because the adapter does not exist.
- [x] Implement only the score calculation, partitioned ordering and validation required by the tests.
- [x] Re-run the focused test and confirm all adapter cases pass.

### Task 2: P6 snapshot integration

**Files:**
- Create: `IntegratedV7MacroExpertSnapshotService.cs`
- Modify: `Tests/IntegratedV7MacroExpertAdapterTests.cs`

**Interfaces:**
- Consumes: adapter result, `VersionedExpertRegistry`, `ImmutableExpertSnapshotStore.FreezeAndAppend`.
- Produces: sealed append-only `ExpertSnapshot` for one target issue and revision.

- [x] Add failing integration tests for valid freeze, invalid cutoff, matching revision/version, immutable duplicate behavior and unchanged PredictionHistory count/content.
- [x] Run the focused test and confirm failure is caused by the missing snapshot service.
- [x] Implement the minimal isolated snapshot service.
- [x] Re-run focused tests and P5/P6 smoke tests.

### Task 3: Admission audit and revision

**Files:**
- Modify: `CurrentExpertCatalog.cs`
- Create: `docs/Integrated-V7-Macro-Admission-Audit.md`
- Modify: `Tests/IntegratedV7MacroExpertAdapterTests.cs`

**Interfaces:**
- Consumes: verified adapter/snapshot evidence.
- Produces: a new append-only Integrated-V7 revision; old `Integrated-V7@f055-o045-v1` remains unchanged.

- [x] Add a failing test asserting both old and new revisions coexist and the new revision remains disabled/ineligible.
- [x] Append the new revision registration and its source evidence without modifying the old revision.
- [x] Run focused tests, P5/P6 tests, full smoke suite and Release build.
- [x] Record exact LeakageAudit and SnapshotIntegrity evidence in the audit report; do not claim Passed if any check fails.
- [x] Inspect git diff and verify no production V7/history/workflow file changed.
