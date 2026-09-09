# V7 云端预测历史安全导入 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让本机在自动学习记忆不同的情况下仍可安全导入云端缺失预测记录。

**Architecture:** 云端日预测摘要继续只缓存，不降低正式历史的完整快照要求。`SymmetricRuntimeStateSync` 改为逐条合并预测、独立处理模型记忆：本机已有且不一致的记忆被保留并计数，不再令整次同步失败。

**Tech Stack:** C#、.NET 10、SQLite、现有 Console SmokeTests。

## Global Constraints

- 不修改 V6.5/V7 预测规则、权重、排序或学习计算。
- 不覆盖、不删除、不重算既有 `PredictionHistory`。
- 不覆盖本机 `ModelMemory`。
- 云端摘要文件不得写入正式历史。

---

### Task 1: 允许冲突记忆下安全合并云端状态

**Files:**
- Modify: `SymmetricRuntimeState.cs:76-110`
- Modify: `Tests/Program.cs:2589-2624`

**Interfaces:**
- Consumes: `SymmetricRuntimeStateSnapshot`、`DatabaseHelper.MergeSynchronizedPrediction`、`DatabaseHelper.LoadModelMemoryJson`。
- Produces: `SymmetricRuntimeStateSync.MergeIntoLocal(SymmetricRuntimeStateSnapshot)` 返回导入的预测记录数；冲突模型记忆保留本机状态且不抛出异常。

- [ ] **Step 1: 写入失败回归测试**

将现有 `SymmetricStateConflictDoesNotPartiallyMerge` 的记忆冲突断言替换为：构造一条本机不存在的云端预测记录和一条冲突记忆，断言调用返回 `1`、新预测记录存在、本机记忆仍为 `{"LearnedSamples":1}`。

```csharp
Assert(SymmetricRuntimeStateSync.MergeIntoLocal(memoryConflict) == 1,
    "模型记忆冲突不应阻断缺失预测记录导入");
Assert(DatabaseHelper.LoadModelMemoryJson(memoryKey) == "{\"LearnedSamples\":1}",
    "冲突时必须保留本机模型记忆");
```

- [ ] **Step 2: 运行测试，确认当前实现失败**

Run: `dotnet run --project Tests/六合分析软件.SmokeTests.csproj --no-restore`

Expected: `同构状态冲突不会部分写入` 因 `MergeIntoLocal` 抛出“模型记忆冲突”失败。

- [ ] **Step 3: 进行最小实现**

在 `MergeIntoLocal` 中移除记忆冲突的 `throw`。保留逐条预测合并逻辑；在写入记忆时，仅当本机值为空才调用 `SaveModelMemoryJson`。对已有且不同的记忆使用 `AppLogger.Info` 记录“保留本机模型记忆”。

- [ ] **Step 4: 运行测试，确认修复通过**

Run: `dotnet run --project Tests/六合分析软件.SmokeTests.csproj --no-restore`

Expected: 所有 SmokeTests 通过，冲突测试证明新预测被导入且本机记忆未改变。

- [ ] **Step 5: 提交最小修复**

```bash
git add SymmetricRuntimeState.cs Tests/Program.cs
git commit -m "fix: import cloud predictions despite local memory conflict"
```

### Task 2: 验证实际云端同步

**Files:**
- No code changes.

**Interfaces:**
- Consumes: `CloudPredictionSyncService.SyncAsync()` 与本机 `history.db`。
- Produces: 本机缓存与正式历史均能包含云端最新缺失期，且本机学习记忆不被覆盖。

- [ ] **Step 1: 构建 V7 桌面程序**

Run: `dotnet build 六合分析软件.csproj -c Release --no-restore --verbosity quiet /clp:ErrorsOnly`

Expected: exit code 0。

- [ ] **Step 2: 运行实际同步并检查日志**

启动桌面程序，等待启动同步完成；检查日志不再出现 `模型记忆冲突` 作为同步失败原因。

- [ ] **Step 3: 检查本地历史与来源**

确认云端最新预测期进入 `PredictionHistory`，来源为“云端同步”；确认已有本机记录与本机模型记忆未变化。

- [ ] **Step 4: 推送已验证修复**

```bash
git push origin main
```

