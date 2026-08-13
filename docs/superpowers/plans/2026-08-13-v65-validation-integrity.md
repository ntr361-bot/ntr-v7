# V6.5 验证完整性修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让自动学习、校准、波色和云端同步不再产生不可验证的历史数据。

**Architecture:** 自动学习记忆库在生成 V6.5 自动学习预测前完成一次可重复的历史预训练；校准查询接收目标期上界；波色来源成为训练过滤条件；云端旧预测保持缓存但不得伪装为已导入学习历史。

**Tech Stack:** .NET 10、SQLite、现有 SmokeTests 控制台测试。

## Global Constraints

- V6.5、V7 智能预测和旧云端档案不可混合为同一模型成绩。
- 历史预测必须仅使用目标期之前已知信息。
- 预测快照首次写入后不可覆盖。

---

### Task 1: 自动学习历史预训练

**Files:**
- Modify: `V7PredictionHistoryService.cs`
- Modify: `AutoLearningEvaluation.cs`
- Test: `Tests/Program.cs`

- [ ] **Step 1: Write the failing test**

```csharp
ModelMemoryState state = V65AutoLearningBootstrap.EnsureInitialTraining();
Assert(state.LearnedSamples > 0, "V6.5 automatic learning must bootstrap from historical V6.5 snapshots");
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project Tests\\六合分析软件.SmokeTests.csproj --no-restore`

- [ ] **Step 3: Write minimal implementation**

```csharp
ModelMemoryState memory = V65AutoLearningBootstrap.EnsureInitialTraining(history);
```

The bootstrap must use the `ExperimentModels.AutoLearning` memory store and persist only after chronological training.

- [ ] **Step 4: Run test to verify it passes**

- [ ] **Step 5: Commit**

### Task 2: 校准截止期

**Files:**
- Modify: `Services/PredictionLearningService.cs`
- Modify: `AIEngine.cs`
- Test: `Tests/Program.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using (DatabaseHelper.UseHistoryThroughIssue(101))
    PredictionLearningService.ApplyCalibration(result, 50);
Assert(result.Top3.SequenceEqual(before), "future verified prediction must not calibrate historical target");
```

- [ ] **Step 2: Run test to verify it fails**

- [ ] **Step 3: Write minimal implementation**

```csharp
ApplyCalibration(result, analysisPeriods, result.TargetIssue);
```

Only numeric stored issue values strictly lower than `TargetIssue` are eligible.

- [ ] **Step 4: Run test to verify it passes**

- [ ] **Step 5: Commit**

### Task 3: 云端同步语义

**Files:**
- Modify: `CloudPredictionSyncService.cs`
- Modify: `Form1.cs`
- Test: `Tests/Program.cs`

- [ ] **Step 1: Write the failing test**

```csharp
Assert(CloudPredictionSyncService.ImportPrediction(legacy) == 0,
    "legacy cloud prediction remains cached, not imported into learning history");
```

- [ ] **Step 2: Run test to verify it passes after UI terminology update**

- [ ] **Step 3: Write minimal implementation**

Change only user-facing wording from imported history to cached cloud prediction files.

- [ ] **Step 4: Run all smoke tests and build**

- [ ] **Step 5: Commit and push after verification**
