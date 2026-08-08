# Automatic Learning Meta Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在保持当前版本、现有预测界面和 V6.3/V8 模块不变的前提下，增加严格无未来数据泄漏的预测反馈、方案 C 元排序模型、自适应权重、持久模型记忆、TOP3 连续 5 期或 TOP6 连续 3 期未命中失效分析和独立学习报告。

**Architecture:** 现有 AI/ML/状态/规则模型继续独立产生 12 生肖分数，新增 `MetaPredictionEngine` 只读取这些基础分数、排名和 `FeatureEngine` 特征组，生成最终 12 生肖排序。开奖后由 `AutoLearningEngine` 幂等回填真实名次、更新元模型与模型记忆，并通过 `WeightOptimizer` 在单次 ±5%、单模型 0–70%、总和 100% 的约束下调整权重；不足 100 个已学习样本或近期表现恶化时自动退回原有排序。

**Tech Stack:** C#、.NET 10 Windows Forms、SQLite、System.Text.Json、现有 LightGBM 风格评分与 V8 状态识别模块、现有 `Tests/六合分析软件.SmokeTests.csproj` 冒烟测试框架。

## Global Constraints

- 不改变当前预测界面，不增加版本号。
- 不删除或重写 V6.3/V8 模块；现有预测逻辑始终作为基础模型和安全回退。
- 不修改已有历史开奖数据；数据库变更只能新增列、表和索引。
- 自动学习使用方案 C：基础模型输出作为元模型输入，最终输出 12 生肖排序，TOP3/TOP6 仅为排名截取。
- 预测第 N 期只能读取 N 期以前的数据；开奖后才允许用 N 期真实结果学习。
- 初始权重 AI=40%、ML=40%、状态=20%、规则=0%；单次调整绝对值不超过 5 个百分点，单项范围 0–70%，总和恒为 100%。
- TOP3 连续 5 期未命中，或 TOP6 连续 3 期未命中，任一满足即触发失效模型与失效特征分析，并降低对应系数。
- 五行不进入特征贡献和元模型输入。
- 学习报告放在现有智能预测历史窗口入口，不在主预测界面增加控件。
- 2023–2025 只按时间顺序学习一次；2026 严格执行“先预测、再揭晓、再学习”的滚动验证。

---

### Task 1: Database migration and persistence contracts

**Files:**
- Modify: `DatabaseHelper.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Produces: `DatabaseHelper.EnsureAutoLearningSchema(SQLiteConnection connection)`
- Produces: `DatabaseHelper.GetPredictionLearningRows()` returning chronological `PredictionLearningRow` values.
- Produces: `DatabaseHelper.SaveLearningAdjustment(LearningAdjustmentRecord record)`.
- Produces schema columns `FinalRankingJson`, `BaseModelScoresJson`, `FeatureSnapshotJson`, `WeightSnapshotJson`, `ActualRank`, `LearningStatus`, `LearnedAt` on `PredictionHistory`.
- Produces tables `ModelMemory(MemoryKey, MemoryJson, UpdatedAt)` and `LearningAdjustmentHistory(Id, Issue, AdjustedAt, OldWeightsJson, NewWeightsJson, FeatureContributionJson, Reason)`.

- [ ] **Step 1: Write failing schema tests**

Add a smoke test that initializes a temporary SQLite database and asserts every new column/table exists, while inserting an old-format prediction row to prove migration is additive.

```csharp
Run("auto learning schema is additive", () =>
{
    using var db = TestDatabase.CreateLegacyPredictionDatabase();
    DatabaseHelper.EnsureAutoLearningSchema(db.Connection);
    Assert.True(db.HasColumn("PredictionHistory", "FinalRankingJson"));
    Assert.True(db.HasColumn("PredictionHistory", "LearningStatus"));
    Assert.True(db.HasTable("ModelMemory"));
    Assert.True(db.HasTable("LearningAdjustmentHistory"));
    Assert.Equal(1, db.Count("PredictionHistory"));
});
```

- [ ] **Step 2: Run the focused test and verify RED**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- auto-learning-schema`

Expected: compilation failure because `EnsureAutoLearningSchema` and the new contracts do not exist.

- [ ] **Step 3: Implement idempotent migration**

Use the existing `AddColumnIfMissing`/schema initialization pattern. Add tables with `CREATE TABLE IF NOT EXISTS`, add indexes on `(LearningStatus, Issue)` and `LearningAdjustmentHistory(Issue)`, and default old rows to `LearningStatus='Pending'` without rewriting prediction content.

```csharp
AddColumnIfMissing(connection, "PredictionHistory", "FinalRankingJson", "TEXT DEFAULT ''");
AddColumnIfMissing(connection, "PredictionHistory", "LearningStatus", "TEXT DEFAULT 'Pending'");
```

- [ ] **Step 4: Run schema tests and existing database tests**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- auto-learning-schema database`

Expected: PASS and legacy rows remain unchanged.

- [ ] **Step 5: Commit only Task 1 files**

```powershell
git add -- DatabaseHelper.cs Tests/Program.cs
git commit -m "feat: add automatic learning persistence schema"
```

### Task 2: Bounded adaptive weight optimizer

**Files:**
- Create: `WeightOptimizer.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Produces: `sealed record ModelWeights(double AI, double ML, double State, double Rule)`.
- Produces: `sealed record ModelFeedback(IReadOnlyDictionary<string,double> Utility, string Reason)`.
- Produces: `ModelWeights WeightOptimizer.Adjust(ModelWeights current, ModelFeedback feedback)`.
- Invariant: each component is `[0, 0.70]`, each delta is at most `0.05`, and the returned sum differs from `1.0` by less than `1e-9`.

- [ ] **Step 1: Write failing invariant tests**

```csharp
Run("weight optimizer enforces limits", () =>
{
    var current = new ModelWeights(.40, .40, .20, 0);
    var next = new WeightOptimizer().Adjust(current,
        new ModelFeedback(new Dictionary<string,double>{{"AI",1},{"ML",-1},{"State",0},{"Rule",0}}, "test"));
    Assert.InRange(Math.Abs(next.AI-current.AI), 0, .05 + 1e-9);
    Assert.InRange(Math.Abs(next.ML-current.ML), 0, .05 + 1e-9);
    Assert.InRange(next.AI, 0, .70);
    Assert.Near(1.0, next.AI+next.ML+next.State+next.Rule, 1e-9);
});
```

- [ ] **Step 2: Run focused test and verify RED**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- weight-optimizer`

Expected: compilation failure because `ModelWeights` and `WeightOptimizer` do not exist.

- [ ] **Step 3: Implement capped-simplex adjustment**

Normalize feedback utility, cap raw movement to ±0.05, clamp each item to `[0, .70]`, then redistribute residual only among components with remaining capacity. Reject NaN/Infinity and return current normalized weights on invalid feedback.

- [ ] **Step 4: Add edge-case tests and run GREEN**

Cover zero feedback, one weight at 70%, one at 0%, all-negative feedback, and invalid numeric input.

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- weight-optimizer`

Expected: all weight tests PASS.

- [ ] **Step 5: Commit Task 2**

```powershell
git add -- WeightOptimizer.cs Tests/Program.cs
git commit -m "feat: add bounded model weight optimizer"
```

### Task 3: Persistent model memory

**Files:**
- Create: `ModelMemory.cs`
- Modify: `DatabaseHelper.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Produces: `sealed class ModelMemoryState` with `Weights`, `MetaCoefficients`, `FeatureContributions`, `LearnedSamples`, `LastTrainingIssue`, `RecentTop3`, `RecentTop6`, `RecentReciprocalRanks`, `ConsecutiveTop3Misses`, and `ConsecutiveTop6Misses`.
- Produces: `ModelMemoryState ModelMemory.LoadOrCreate()`.
- Produces: `void ModelMemory.Save(ModelMemoryState state)`.
- Default state uses `.40/.40/.20/.00` and empty bounded recent metric queues.

- [ ] **Step 1: Write failing round-trip tests**

Create an in-memory store, save a state with non-default weights/coefficient/contribution/streak, reload, and assert exact round trip. Also test malformed JSON returns the default state without deleting the malformed row.

- [ ] **Step 2: Run test and verify RED**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- model-memory`

Expected: compilation failure because `ModelMemory` does not exist.

- [ ] **Step 3: Implement JSON persistence**

Use `System.Text.Json`, key `auto-learning-meta-v1`, an injectable persistence delegate for tests, atomic `INSERT ... ON CONFLICT(MemoryKey) DO UPDATE`, and validation that clamps weights and caps recent histories at 500 entries.

- [ ] **Step 4: Run round-trip and corruption tests**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- model-memory`

Expected: PASS.

- [ ] **Step 5: Commit Task 3**

```powershell
git add -- ModelMemory.cs DatabaseHelper.cs Tests/Program.cs
git commit -m "feat: persist automatic learning model memory"
```

### Task 4: Meta-model input snapshots and feature-group contributions

**Files:**
- Modify: `FeatureEngine.cs`
- Modify: `MachineLearningPredictionService.cs`
- Modify: `MarketStateEngine.cs`
- Create: `MetaPredictionEngine.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Produces: `sealed record ZodiacMetaFeatures(string Zodiac, IReadOnlyDictionary<string,double> BaseScores, IReadOnlyDictionary<string,double> FeatureGroups)`.
- Produces: `sealed record MetaPredictionInput(string Issue, IReadOnlyList<ZodiacMetaFeatures> Zodiacs)` containing exactly 12 unique zodiacs.
- Produces: `FeatureEngine.BuildGroupedContributions(...)` with groups `frequency`, `omission`, `cycle`, `momentum`, `repeat`, `trend`, `market_state`, `model_consensus`.
- Produces no five-element feature group.

- [ ] **Step 1: Write failing snapshot validation tests**

Assert that a valid snapshot contains 12 zodiacs and AI/ML/State/Rule base scores; duplicate/missing zodiacs are rejected; group output contains the eight named groups and no `five_element` key.

- [ ] **Step 2: Run focused test and verify RED**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- meta-snapshot`

Expected: compilation failure or missing group API.

- [ ] **Step 3: Add normalized score extraction**

Expose score-only adapters around existing engines without changing their current public prediction behavior. Normalize each source independently to `[0,1]`; missing sources are marked absent rather than fabricated.

- [ ] **Step 4: Add grouped feature contribution builder**

Map existing non-five-element features to the eight stable groups. Each group score must be finite and clipped to `[-1,1]`; `model_consensus` uses base-score rank agreement and spread.

- [ ] **Step 5: Run tests and commit Task 4**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- meta-snapshot feature-groups`

Expected: PASS.

```powershell
git add -- FeatureEngine.cs MachineLearningPredictionService.cs MarketStateEngine.cs MetaPredictionEngine.cs Tests/Program.cs
git commit -m "feat: build meta prediction feature snapshots"
```

### Task 5: Scheme C meta ranking and safe fallback

**Files:**
- Modify: `MetaPredictionEngine.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Produces: `sealed record RankedZodiac(string Zodiac, double Probability, int Rank)`.
- Produces: `sealed record MetaPredictionResult(IReadOnlyList<RankedZodiac> Ranking, bool UsedFallback, string FallbackReason)`.
- Produces: `MetaPredictionResult MetaPredictionEngine.Predict(MetaPredictionInput input, ModelMemoryState memory, IReadOnlyList<string> baselineRanking)`.
- Produces: `void MetaPredictionEngine.Learn(MetaPredictionInput input, string actualZodiac, ModelMemoryState memory)`.

- [ ] **Step 1: Write failing ranking/fallback tests**

Test deterministic 12-item descending probability order, probabilities summing to 1, ties broken by baseline order, learning moving the actual zodiac score upward, and fallback when learned samples are below 100.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- meta-ranking`

Expected: missing `Predict`/`Learn` implementation.

- [ ] **Step 3: Implement weighted stacking scorer**

Compute each zodiac logit from weighted AI/ML/State/Rule normalized scores plus persisted feature-group coefficients. Apply numerically stable softmax and rank all 12.

- [ ] **Step 4: Implement fallback gates**

Return baseline ranking when any condition holds: `<100` learned samples, incomplete 12-zodiac scores, invalid coefficients, or recent 30 TOP6 hit rate trails baseline by more than 0.10. Include a machine-readable reason.

- [ ] **Step 5: Implement one-pass online coefficient update**

After truth is known, calculate ranking loss gradient only for that issue, use a bounded learning rate, clip coefficient deltas, and update contribution aggregates. Never read a later issue.

- [ ] **Step 6: Run tests and commit Task 5**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- meta-ranking meta-fallback`

Expected: PASS.

```powershell
git add -- MetaPredictionEngine.cs Tests/Program.cs
git commit -m "feat: add safe scheme c meta ranking"
```

### Task 6: Feedback learning and dual-threshold failure analysis

**Files:**
- Create: `AutoLearningEngine.cs`
- Modify: `DatabaseHelper.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Produces: `sealed record LearningOutcome(bool Updated, bool FailureAnalysisTriggered, int ActualRank, ModelWeights Weights, string Reason)`.
- Produces: `LearningOutcome AutoLearningEngine.ApplyActualResult(PredictionLearningRow prediction, string actualZodiac)`.
- Produces: `FailureAnalysis AutoLearningEngine.AnalyzeFailureWindow(IReadOnlyList<PredictionLearningRow> rows, FailureTrigger trigger)`.
- Idempotency key: a row is learned exactly once when `LearningStatus` changes from `Pending` to `Learned` in the same transaction as memory and adjustment history.

- [ ] **Step 1: Write failing feedback tests**

Cover TOP3/TOP6/rank calculation, repeated feedback not changing weights twice, TOP3 four misses not triggering and five misses triggering once, TOP6 two misses not triggering and three misses triggering once, and a later hit resetting only its corresponding streak.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- auto-feedback failure-thresholds`

Expected: compilation failure because `AutoLearningEngine` does not exist.

- [ ] **Step 3: Implement transactional feedback**

Load the saved snapshot, locate actual rank, update row truth fields, call meta learning, update recent metrics and persist memory inside one transaction. If snapshot is absent, mark `SkippedLegacy` without inventing scores.

- [ ] **Step 4: Implement dual-threshold attribution**

On `ConsecutiveTop3Misses == 5`, analyze only the five-row TOP3 failure window. On `ConsecutiveTop6Misses == 3`, analyze only the three-row TOP6 failure window. For the triggered window, compare each base model's actual ranks and each feature group's signed contribution, build negative utility for the weakest base source, and reduce the most consistently misleading feature coefficient. Persist before/after weights and the trigger reason. Do not trigger the same threshold again later in the same miss streak; TOP3 and TOP6 hit events reset only their own counters.

- [ ] **Step 5: Run tests and commit Task 6**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- auto-feedback failure-thresholds idempotent`

Expected: PASS and all weight invariants remain true.

```powershell
git add -- AutoLearningEngine.cs DatabaseHelper.cs Tests/Program.cs
git commit -m "feat: learn from top3 and top6 miss thresholds"
```

### Task 7: Integrate snapshots, prediction saving, and verification

**Files:**
- Modify: `AIEngine.cs`
- Modify: `DatabaseHelper.cs`
- Modify: `Services/PredictionLearningService.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Consumes: `MetaPredictionEngine.Predict`, `ModelMemory.LoadOrCreate`, `AutoLearningEngine.ApplyActualResult`.
- Preserves: existing `AIEngine.Predict(...)`, `AIEngine.SavePredictionHistory(PredictResult)`, and UI-facing result contracts.
- Produces: saved final ranking/base scores/features/weights on every new eligible prediction.

- [ ] **Step 1: Write failing integration tests**

Create a chronological fixture through issue N, predict N+1, assert every snapshot excludes N+1 truth, save, verify the issue, then assert one learned row and one memory update. Repeat verification and assert no second update.

- [ ] **Step 2: Run integration test and verify RED**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- auto-learning-integration`

Expected: snapshots are not yet saved or feedback is not invoked.

- [ ] **Step 3: Integrate meta inference behind fallback**

Build the snapshot from data strictly before target issue. Ask the meta engine for a ranking; if fallback is active, preserve the exact existing ranking. Populate the new JSON fields without changing displayed version strings or current prediction controls.

- [ ] **Step 4: Integrate verification callback**

After existing `VerifyPrediction` updates the real result, invoke `AutoLearningEngine` for eligible pending rows. Preserve the current `PredictionLearningService` review text; automatic learning metadata is additive.

- [ ] **Step 5: Run integration plus all existing prediction tests**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- prediction auto-learning-integration`

Expected: PASS and existing output contracts unchanged.

- [ ] **Step 6: Commit Task 7**

```powershell
git add -- AIEngine.cs DatabaseHelper.cs Services/PredictionLearningService.cs Tests/Program.cs
git commit -m "feat: connect prediction feedback learning"
```

### Task 8: Automatic learning report window

**Files:**
- Create: `AutoLearningReportForm.cs`
- Modify: `AIPredictHistoryForm.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Produces: `sealed record AutoLearningReportData(...)` containing current status, recent-100 TOP3/TOP6/MRR/max miss streak, current weights, top three feature contributions, and recent adjustments.
- Produces: `AutoLearningReportForm(AutoLearningReportData data)`.
- Adds a `学习报告` button only when the existing history window is in intelligent/new-model mode.

- [ ] **Step 1: Write failing report-data tests**

Construct 100 synthetic learned rows and assert metric calculations, weight percentages, top-three contribution ordering, and adjustment reverse chronology.

- [ ] **Step 2: Run test and verify RED**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- learning-report`

Expected: report types do not exist.

- [ ] **Step 3: Implement read-only report service and form**

Use existing WinForms styling. The report must never trigger training or change memory. Show explicit `样本不足` when fewer than 100 learned rows exist.

- [ ] **Step 4: Add history-window entry without altering main UI**

Place `学习报告` beside the current refresh/history controls only for the intelligent prediction history mode; open the new form modally.

- [ ] **Step 5: Run tests and commit Task 8**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- learning-report ui-contracts`

Expected: PASS.

```powershell
git add -- AutoLearningReportForm.cs AIPredictHistoryForm.cs Tests/Program.cs
git commit -m "feat: add automatic learning report window"
```

### Task 9: Strict 2023–2026 evaluation runner and report

**Files:**
- Create: `AutoLearningEvaluation.cs`
- Modify: `Tests/Program.cs`
- Create: `Auto Learning Evaluation Report.md`
- Create: `Auto Learning Evaluation Results.json`

**Interfaces:**
- Produces: `AutoLearningEvaluation.Run(IReadOnlyList<LotteryRecord> chronological)`.
- Produces baseline and learning metrics: TOP3, TOP6, MRR, maximum consecutive TOP6 misses, yearly counts, and fallback count.
- Training protocol: consume 2023–2025 once in order; for each 2026 issue predict before revealing truth, score, then learn.

- [ ] **Step 1: Write leakage-guard tests**

Instrument the runner with an observer that records the maximum issue visible at each prediction and assert it is strictly less than the target issue. Assert 2023–2025 samples are visited once, not retrained before every 2026 issue.

- [ ] **Step 2: Run focused test and verify RED**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- evaluation-no-leakage`

Expected: evaluator does not exist.

- [ ] **Step 3: Implement chronological evaluator**

Clone memory for baseline and learning arms. Baseline uses unchanged current ranking. Learning arm runs saved meta model state. Score both on the same 2026 issues; reveal truth only after both predictions.

- [ ] **Step 4: Run full historical evaluation**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- evaluate-auto-learning`

Expected: command writes deterministic JSON and Markdown reports with actual counts and metrics. It must not claim improvement when metrics do not improve.

- [ ] **Step 5: Inspect report invariants**

Assert JSON has nonzero 2026 sample count, identical issue sets in baseline/learning, all rates `[0,1]`, MRR `[1/12,1]`, and no future-data violation.

- [ ] **Step 6: Commit Task 9**

```powershell
git add -- AutoLearningEvaluation.cs Tests/Program.cs "Auto Learning Evaluation Report.md" "Auto Learning Evaluation Results.json"
git commit -m "test: evaluate automatic learning without leakage"
```

### Task 10: Full verification and release artifact

**Files:**
- Modify only if failures reveal defects in files changed by Tasks 1–9.

**Interfaces:**
- Verifies all existing and new smoke tests, database migration against a copy of the runtime database, report read-only behavior, and a Windows release build.

- [ ] **Step 1: Restore and run all smoke tests**

Run: `dotnet restore Tests\六合分析软件.SmokeTests.csproj --ignore-failed-sources`

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj`

Expected: zero failed tests; retain the exact pass/fail count in the handoff.

- [ ] **Step 2: Build to an isolated verification directory**

Run: `dotnet build 六合分析软件.csproj -c Release -o D:\projects\六合分析软v6.3正式版\verify-auto-learning`

Expected: 0 errors. Existing known warnings may remain, but no new warning category from the added files.

- [ ] **Step 3: Verify migration against a copied runtime database**

Copy `bin\Debug\net10.0-windows\data\history.db` to a temporary directory, launch schema initialization against the copy, assert row counts and historical draw contents are unchanged, then delete only the temporary copy.

- [ ] **Step 4: Verify UI launch and report opening**

Launch the isolated executable, open intelligent prediction history, open `学习报告`, confirm the main prediction form has no new controls and the report does not mutate memory timestamps.

- [ ] **Step 5: Review the exact diff**

Run: `git diff --check`

Run: `git status --short`

Expected: no whitespace errors; unrelated pre-existing user modifications remain preserved.

- [ ] **Step 6: Final commit and handoff**

Commit only the automatic-learning implementation files still uncommitted. Report modified file list, automatic-learning flow, actual evaluation comparison, whether prediction metrics improved, fallback behavior, and test/build evidence.
