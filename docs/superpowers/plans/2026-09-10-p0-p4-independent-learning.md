# P0–P4 Independent Learning Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build P0–P4 as a third durable shadow learner, with no formal-prediction side effects.

**Architecture:** `IndependentP0P4Store` owns `data/experiments/p0p4-independent-learning/p0p4-learning.db`. `IndependentP0P4Service` freezes pre-draw snapshots, learns after draws, and recovers sequential gaps. It uses existing V6.5 input construction read-only.

**Tech Stack:** C#, .NET 10, System.Data.SQLite, WinForms, existing V65 experiment helpers.

## Global Constraints

- Model key: `V7-IndependentLearning-P0P4`.
- Target N only reads history with issue `< N`.
- Never write formal `PredictionHistory`, `ModelMemory`, V6.5/V7 weights, runtime state, cloud data or Smart Ledger data.
- Reconstructed records are marked and cannot be live predictions.
- Each behavior is implemented red → green.

---

## File structure

- Create `IndependentP0P4Contracts.cs`: immutable snapshot, shadow, audit, health and outcome records.
- Create `IndependentP0P4Store.cs`: dedicated SQLite schema and append-only persistence.
- Create `IndependentP0P4Service.cs`: prediction, learning, recovery and status.
- Create `IndependentP0P4DailyShadow.cs`: daily non-production entry.
- Create `Tests/IndependentP0P4Tests.cs`: isolated acceptance suite.
- Modify `Tests/Program.cs`: `--p0-p4-independent-smoke` only.
- Modify `Form1.cs`: read-only status entry after acceptance.

### Task 1: P0 isolated storage

**Files:** Create `IndependentP0P4Contracts.cs`, `IndependentP0P4Store.cs`; test `Tests/IndependentP0P4Tests.cs`.

- [ ] Write a failing test that constructs `new IndependentP0P4Store(testRoot)` and asserts its database is `data/experiments/p0p4-independent-learning/p0p4-learning.db`, differs from `DatabaseHelper.DatabasePath`, and exposes model key `V7-IndependentLearning-P0P4`.
- [ ] Run `dotnet run --project Tests/六合分析软件.SmokeTests.csproj -c Release -- --p0-p4-independent-smoke`; confirm compilation fails because the store does not exist.
- [ ] Implement model identity, create the dedicated directory/database, and create only `P0P4Snapshot`, `P0P4ShadowPrediction`, `P0P4Audit`, `P0P4Receipt`, and `P0P4Memory` tables.
- [ ] Re-run the smoke test; expect `PASS P0 store is separate`.
- [ ] Commit `feat: add isolated P0-P4 learning store`.

### Task 2: P1 frozen pre-draw snapshots

**Files:** Modify `IndependentP0P4Contracts.cs`, `IndependentP0P4Store.cs`; create `IndependentP0P4Service.cs`; test `Tests/IndependentP0P4Tests.cs`.

- [ ] Write a failing test: `CreateShadow(2026101, historyThrough2026100)` returns exactly 12 unique zodiac ranks and a hash; passing history that includes issue 2026101 throws; appending the same snapshot is idempotent; differing content for the same issue throws.
- [ ] Run the smoke test and confirm failure because `CreateShadow` is absent.
- [ ] Implement `CreateShadow(long targetIssue, IReadOnlyList<HistoryRecord> prefix)`: require `max(prefix) < targetIssue`, build with independent memory, validate full ranking, calculate canonical SHA-256, and append snapshot plus shadow record. Do not call formal save APIs.
- [ ] Re-run smoke test; expect all P1 assertions pass.
- [ ] Commit `feat: freeze P0-P4 shadow predictions`.

### Task 3: P2 one-time post-draw learning

**Files:** Modify `IndependentP0P4Store.cs`, `IndependentP0P4Service.cs`, `Tests/IndependentP0P4Tests.cs`.

- [ ] Write a failing test: after a frozen prediction and revealed actual zodiac, `LearnRevealedIssue` increments independent `MemoryVersion` once, writes one audit including actual rank, and a second call returns `AlreadyLearned` with identical memory.
- [ ] Run the smoke test and confirm failure because learning receipts do not exist.
- [ ] Implement `LearnRevealedIssue`: in one transaction read independent memory/snapshot, require actual draw, train copied independent memory through `AutoLearningTrainer.LearnOne`, append audit and receipt, then commit. On failure roll back memory/receipt and append only failure audit.
- [ ] Re-run smoke test; expect P2 assertions pass.
- [ ] Commit `feat: audit isolated P0-P4 learning`.

### Task 4: P3 sequential recovery

**Files:** Modify `IndependentP0P4Service.cs`, `IndependentP0P4Store.cs`, `Tests/IndependentP0P4Tests.cs`.

- [ ] Write a failing test: trying to learn 2026103 while 2026102 is missing throws; `CatchUp(historyThrough2026103, true)` causally reconstructs 2026102, marks it `Reconstructed=true` and `IsLivePrediction=false`, then advances cursor through 2026103.
- [ ] Run smoke test and confirm failure because recovery is absent.
- [ ] Implement `CatchUp`: always process the first issue after independent cursor. Reconstruction uses only the pre-issue prefix and preceding independent memory. It cannot write formal prediction rows.
- [ ] Re-run smoke test; expect P3 assertions pass.
- [ ] Commit `feat: recover isolated P0-P4 learning gaps`.

### Task 5: P4 daily Shadow and regression acceptance

**Files:** Create `IndependentP0P4DailyShadow.cs`; modify `Tests/IndependentP0P4Tests.cs`, `Tests/Program.cs`, `Form1.cs`.

- [ ] Write a failing test: capture formal state fingerprint (formal prediction rows, formal memories and runtime state); run `IndependentP0P4DailyShadow.Run(2026104, historyThrough2026103, testRoot)`; assert a live independent shadow exists and the formal fingerprint is byte-for-byte unchanged.
- [ ] Run smoke test and confirm failure because daily entry does not exist.
- [ ] Implement `Run` to catch up revealed independent learning then create only next shadow. Add a Form1 status action that displays latest shadow issue, last trained issue, memory version and health; it must not invoke learning or formal prediction.
- [ ] Run `dotnet build 六合分析软件.csproj -c Release --no-restore` and then the full smoke suite. Expect successful build and unchanged formal fingerprint.
- [ ] Commit `feat: run P0-P4 independent learning shadow`.

## Self-review

- The plan covers storage, pre-draw freeze, post-draw learning, recovery, daily shadow, UI status, persistence and regression isolation.
- Macro, COUNTER, formal V6.5/V7, cloud publication and Smart Ledger are outside this implementation.
- Names and paths match the approved design and no task permits formal data mutation.
