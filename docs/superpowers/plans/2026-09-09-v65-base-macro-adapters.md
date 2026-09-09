# V65 Base Macro Adapters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Admit the three V6.5 base experts technically by producing deterministic immutable full-ranking snapshots from caller-supplied historical prefixes without changing production predictions.

**Architecture:** A versioned definition catalog selects the existing V6.5 window and fixed weights. A pure adapter delegates scoring to `V65RuleScoringEngine`; a separate snapshot service validates temporal provenance and appends sealed live or reconstructed snapshots to the existing P6 store.

**Tech Stack:** C#/.NET, SQLite-backed P5/P6 stores, existing console smoke-test harness.

## Global Constraints

- Do not modify V6.5 scoring formulas, weights, formal ranking, PredictionHistory, Auto models, P15+, or production integration.
- Read only the caller-supplied history prefix and require `HistoryCutoffIssue < TargetIssue`.
- Keep all new expert revisions disabled and ineligible for Macro.

---

### Task 1: Specify adapter and snapshot behavior with failing tests

**Files:**
- Create: `Tests/V65BaseMacroExpertAdapterTests.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Consumes: existing `V65RuleScoringEngine`, `ImmutableExpertSnapshotStore`.
- Produces: executable `--v65-base-macro-adapter-smoke` acceptance test.

- [x] Add assertions for exact formal ranking parity, FullRanking12, determinism, temporal rejection, sealed hash, immutable append, conflicting overwrite rejection, causal reconstruction, catalog append-only status, and unchanged PredictionHistory.
- [x] Run the focused test and confirm it fails because the new adapter/service types do not exist.

### Task 2: Implement the pure V65 base adapter

**Files:**
- Create: `V65BaseMacroExpertAdapter.cs`

**Interfaces:**
- Consumes: `Build(history, expertId)` and existing fixed weight definitions.
- Produces: `V65BaseMacroRanking` with exactly 12 ranked zodiac items.

- [x] Add immutable definitions for the 50, 100, and all-history experts with explicit revision and algorithm versions.
- [x] Delegate scoring to the existing explicit-history overload and validate the complete ranking.
- [x] Run the focused test and retain failures that specifically require snapshot persistence.

### Task 3: Implement immutable live and causal-reconstruction snapshots

**Files:**
- Create: `V65BaseMacroExpertSnapshotService.cs`

**Interfaces:**
- Consumes: caller prefix, expert ID, target/cutoff issues, timestamps, and code version.
- Produces: P6-sealed `ExpertSnapshot` via `FreezeAndAppend`.

- [x] Validate every input period, strict cutoff ordering, exact cutoff coverage, and reconstruction provenance.
- [x] Compute a deterministic history-prefix hash and persist live/reconstructed snapshots through the P6 store.
- [x] Run the focused test to green.

### Task 4: Append audited expert revisions

**Files:**
- Modify: `CurrentExpertCatalog.cs`

**Interfaces:**
- Consumes: static definitions from `V65BaseMacroExpertAdapter`.
- Produces: three append-only Passed/Passed registrations that remain disabled and ineligible.

- [x] Append the new revisions without changing dependency edges bound to old production revisions.
- [x] Run P5, P6, Integrated-V7, and V65-focused tests.

### Task 5: Verify and report admission

**Files:**
- Create: `docs/V65-Base-Experts-Macro-Admission-Report.md`

**Interfaces:**
- Consumes: fresh test/build evidence.
- Produces: per-expert PASS/FAIL report and isolation statement.

- [x] Run the full smoke-test suite and Release build.
- [x] Inspect the diff for forbidden production changes and unrelated user files.
- [x] Record exact revisions, audit status, evidence, and scope exclusions in the report.
