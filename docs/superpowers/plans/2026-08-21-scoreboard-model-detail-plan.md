# V7 实验成绩榜按模型近 30 期明细 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 V7 成绩榜可逐模型选择，并以只读窗口查看该模型最近 30 条已开奖预测明细。

**Architecture:** 在 `V65ExperimentScoreboardService` 中公开稳定的模型定义和只读明细查询，将现有实际名次回退规则复用于明细。`V65ExperimentScoreboardView` 保留汇总表，在其首列添加选择框和明细按钮；明细由独立的 WinForms 窗口渲染。汇总始终只计算勾选模型，绝不写库。

**Tech Stack:** .NET 10、C#、WinForms、SQLite、现有烟雾测试程序。

## Global Constraints

- 仅查询现有 `PredictionHistory`，不得写入任何数据库记录。
- 不改变预测、在线学习、权重、云端同步或历史预测。
- 最近 30 条仅限已开奖且可推导实际名次的该模型记录，按期号倒序展示。
- V6.5 与 V7 模型继续按现有模型键独立匹配，不得混算。

---

### Task 1: 只读近 30 期明细查询

**Files:**

- Modify: `V65ExperimentScoreboardService.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**

- Produces: `V65ExperimentScoreboardDetailRow`，包含期号、模型名、Top3/Top6、实际生肖、实际名次、命中标识、预测时间和来源。
- Produces: `GetRecentVerifiedDetails(string modelName, IReadOnlyList<DatabaseHelper.PredictionRecord> records, int limit = 30)`。

- [ ] **Step 1: Write the failing test**

新增一组含 31 条 100 期记录及其他模型记录的测试；断言查询只返回 30 条、均可验证、均为指定模型、按期号倒序。

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project Tests\\六合分析软件.SmokeTests.csproj --no-restore`

Expected: FAIL because `GetRecentVerifiedDetails` does not exist.

- [ ] **Step 3: Write minimal implementation**

按现有 `Definitions` 匹配模型，复用 `ActualRank`；筛除无实际名次的记录，按数值期号倒序取限制数量，并映射为不可变明细记录。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project Tests\\六合分析软件.SmokeTests.csproj --no-restore`

Expected: PASS.

- [ ] **Step 5: Commit**

Commit message: `feat: expose scoreboard model detail records`

### Task 2: 成绩榜选择与明细窗口

**Files:**

- Modify: `V65ExperimentScoreboardView.cs`
- Modify: `Form1.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**

- Consumes: `V65ExperimentScoreboardService.GetRecentVerifiedDetails`。
- Produces: 选择列、明细按钮和只读明细窗体。

- [ ] **Step 1: Write the failing test**

创建成绩榜控件；断言表格含 `Selected` 复选框列与 `Details` 明细列，且所有行初始为已勾选。

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project Tests\\六合分析软件.SmokeTests.csproj --no-restore`

Expected: FAIL because the selection and detail columns do not exist.

- [ ] **Step 3: Write minimal implementation**

在首列插入默认勾选的复选框；勾选状态仅作为界面统计显示的选择状态。每行添加“近30期明细”按钮；点击后弹出只读表格，显示期号、Top3、Top6、实际生肖、实际名次、Top3/Top6 命中、预测时间、来源。无数据时显示“暂无已开奖记录”。将数据中心标题改为 V7。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project Tests\\六合分析软件.SmokeTests.csproj --no-restore`

Expected: PASS.

- [ ] **Step 5: Run full verification**

Run: `dotnet build 六合分析软件.sln --no-restore; dotnet run --project Tests\\六合分析软件.SmokeTests.csproj --no-restore`

Expected: build has zero errors and all smoke tests pass.

- [ ] **Step 6: Commit**

Commit message: `feat: add selectable scoreboard model details`
