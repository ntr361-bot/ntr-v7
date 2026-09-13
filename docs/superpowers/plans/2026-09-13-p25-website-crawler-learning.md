# P25 网站资料爬取与自学习 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Archive website source material by issue, learn per-source reliability from website-published results, and use those weights for future P25-Web predictions.

**Architecture:** Keep website parsing isolated by issue. Add a small SQLite-backed archive for raw source blocks and result settlements, then calculate conservative per-source weights from settled records. The scheduled P25 publication archives all discoverable issue blocks, settles revealed results, and freezes the exact sources and weights used for its target prediction.

**Tech Stack:** C# 13, .NET 10 Windows, System.Data.SQLite, System.Text.Json, existing console smoke tests.

## Global Constraints

- Website data and website-published results establish the initial learning baseline.
- Historical records are parsed and settled per issue; prior issue material never becomes current-issue input.
- Preserve raw blocks, hashes and capture times; never overwrite an earlier snapshot.
- A local History result is a cross-check only and cannot overwrite website source evidence.
- Skip conflicted website/local results during automatic weighting.
- Do not overwrite an existing P25-Web prediction, including 2026256.

---

### Task 1: Parse every issue block and its possible result

**Files:**
- Modify: `WebsiteLearningService.cs`
- Modify: `Tests/MacroP21P25Tests.cs`

**Interfaces:**
- Produces: `WebsiteLearningParser.ParseAll(string text, string sourceId, string sourceHash)` returning immutable issue-level snapshots.
- Produces: an issue snapshot containing `Issue`, `Zodiacs`, `RawText`, optional `WebsiteResultZodiac`, and `SourceHash`.

- [ ] **Step 1: Write the failing test**

Use a multi-period fixture where 256 has `开：？00` and 255 has `开：猪44`. Assert two distinct snapshots; 256 has no result and 255 has result `猪`, without `猪` becoming a 256 input.

- [ ] **Step 2: Run test to verify RED**

Run `dotnet run --project Tests\六合分析软件.SmokeTests.csproj --no-restore -- --p21-p25-smoke`. Expected: FAIL because `ParseAll` is absent.

- [ ] **Step 3: Write minimal implementation**

Enumerate issue-marker blocks, parse recommendations and non-placeholder `开` values only in each block, and keep target parsing as a projection over the same parser.

- [ ] **Step 4: Run test to verify GREEN**

Run the same focused smoke test. Expected: `P21_P25_SMOKE_PASS`.

- [ ] **Step 5: Commit**

Stage `WebsiteLearningService.cs` and `Tests\MacroP21P25Tests.cs`, then commit `feat: parse website learning blocks by issue`.

### Task 2: Persist immutable captures and settlements

**Files:**
- Modify: `DatabaseHelper.cs`
- Modify: `WebsiteLearningService.cs`
- Modify: `Tests/MacroP21P25Tests.cs`

**Interfaces:**
- Produces: `WebsiteLearningArchive.SaveCapture(...)` and `WebsiteLearningArchive.Settle(...)`.
- Consumes: complete issue, source id, hash, raw block, zodiac inputs and optional website result.

- [ ] **Step 1: Write the failing test**

Use a temporary SQLite database: saving the same `(Issue, SourceId, SourceHash)` twice yields one capture; a new hash creates a second version. Settle one capture and assert result, Top3/Top6 hits and local-history consistency are retained.

- [ ] **Step 2: Run test to verify RED**

Run the focused smoke test. Expected: FAIL because archive tables and API are absent.

- [ ] **Step 3: Write minimal implementation**

Create `WebsiteLearningCapture` with unique `(Issue, SourceId, SourceHash)` and `WebsiteLearningSettlement` with one row per capture. Use parameterized writes; preserve website result and mark local History disagreement as `Conflict` without editing History.

- [ ] **Step 4: Run test to verify GREEN**

Run the focused smoke test. Expected: `P21_P25_SMOKE_PASS`.

- [ ] **Step 5: Commit**

Stage the three files and commit `feat: archive and settle website learning evidence`.

### Task 3: Calculate conservative per-source weights and weighted ranking

**Files:**
- Modify: `WebsiteLearningService.cs`
- Modify: `Tests/MacroP21P25Tests.cs`

**Interfaces:**
- Produces: `WebsiteLearningWeightService.Calculate()` keyed by source id.
- Consumes: non-conflicted settlements and returns normalized weight, sample count, Top3/Top6 measures.
- Produces: `WebsiteLearningService.Rank(signals, issue, weights)`.

- [ ] **Step 1: Write the failing test**

Give source A repeated Top3 hits, source B repeated misses and source C one sample. Assert A's bounded weight is higher, while C remains near neutral.

- [ ] **Step 2: Run test to verify RED**

Run the focused smoke test. Expected: FAIL because ranking remains unweighted.

- [ ] **Step 3: Write minimal implementation**

Compute recency-weighted Top3/Top6 success, shrink toward neutral for small samples, normalize usable sources, ignore conflicts, and make ranking add source weights.

- [ ] **Step 4: Run test to verify GREEN**

Run the focused smoke test. Expected: `P21_P25_SMOKE_PASS` and source A affects ranking more.

- [ ] **Step 5: Commit**

Stage the two files and commit `feat: weight website sources from settled history`.

### Task 4: Run crawler-learning before P25 publication

**Files:**
- Modify: `WebsiteLearningService.cs`
- Modify: `Tests/MacroP21P25Tests.cs`

**Interfaces:**
- `WebsiteLearningIntegration.Publish(long targetIssue)` archives all fetched blocks, settles known website results, calculates weights, and writes one first-time target prediction only when target signals exist.

- [ ] **Step 1: Write the failing test**

Inject fixture scripts for two historical periods and one current period. Assert historical blocks archive and settle before ranking, details include source hashes and weight snapshot, and a pre-existing target P25-Web row prevents a write.

- [ ] **Step 2: Run test to verify RED**

Run the focused smoke test. Expected: FAIL because fetching currently projects target issue only.

- [ ] **Step 3: Write minimal implementation**

Return all parsed blocks from fetch, archive and settle them, calculate weights, then project and freeze only the requested target issue. Include capture identifiers/hashes and weight values in details JSON.

- [ ] **Step 4: Run tests and release build**

Run the focused smoke test and `dotnet build 六合分析软件.csproj -c Release --no-restore --verbosity quiet /clp:ErrorsOnly`. Expected: both exit 0.

- [ ] **Step 5: Commit**

Stage the two files and commit `feat: run website crawler learning before P25 prediction`.

### Task 5: Runtime integrity verification

**Files:**
- Verify: `C:\Users\Administrator\AppData\Local\六合分析软件-V7\history.db`
- Verify: `WebsiteLearningService.cs`

- [ ] **Step 1: Capture state**

Record runtime database SHA256 and the frozen 2026256 P25-Web record before any live crawl.

- [ ] **Step 2: Confirm no-overwrite behavior**

Run fixture tests only; confirm no test writes the runtime database.

- [ ] **Step 3: Final checks**

Run `git diff --check`, focused smoke test and release build. Re-read runtime SHA256 and frozen row. Expected: source changes pass and frozen evidence is unchanged.
