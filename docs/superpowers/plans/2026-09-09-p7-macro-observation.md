# P7 Macro Observation Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a deterministic, leakage-safe, sidecar `MacroObservationEngine` that converts a caller-supplied historical prefix and dynamic expert snapshots into auditable facts without changing any prediction.

**Architecture:** P7 consumes the frozen P5 registry plus a `PrefixContext`; it does not own snapshot storage, expert selection, prediction, learning, or production wiring. It validates that every result and snapshot was available before the target prediction, computes only facts supported by common revealed samples, emits `null` with zero samples when a source signal is unavailable, and hashes a canonical ordered payload for reproducibility.

**Tech Stack:** C# 14 / .NET 10, immutable Macro contracts, SQLite-backed `VersionedExpertRegistry`, console smoke tests.

## Global Constraints

- Do not implement P6 snapshot persistence, Gating, COUNTER, hypotheses, weight learning, or production integration.
- Do not modify formal V7/V6.5 algorithms, Top3/Top6, daily workflow, or the existing 17-issue experiment.
- Prediction for issue N may use only results and snapshots available before N and no later than `PrefixContext.AsOf`.
- Missing inputs are `null`/zero-sample observations, never fabricated values or zero performance.
- Dynamic N experts are identified by `ExpertId` and the one `ExpertRevisionId` frozen in the pool.

---

### Task 1: Prefix safety and deterministic observation API

**Files:**
- Create: `Tests/MacroObservationEngineTests.cs`
- Modify: `Tests/Program.cs`
- Create: `MacroObservationEngine.cs`

**Interfaces:**
- Consumes: `IMacroObservationEngine.Observe(PrefixContext)`, `VersionedExpertRegistry`.
- Produces: `MacroObservationEngine.Observe(PrefixContext)` and `RegisteredExpertDependencyAnalyzer.Analyze(PrefixContext)`.

- [x] **Step 1: Write failing tests** for target/future results, future-available snapshots, invalid history cutoffs, duplicate revisions, malformed rankings, and deterministic hashes.
- [x] **Step 2: Run** `dotnet run --project Tests\六合分析软件.SmokeTests.csproj -- --p7-observation-smoke` and verify failure because P7 is absent.
- [x] **Step 3: Implement minimal prefix validation and canonical ordering/hash.**
- [x] **Step 4: Re-run the P7 smoke test and keep the new safety cases green.**

### Task 2: Dynamic expert and dependency observations

**Files:**
- Modify: `Tests/MacroObservationEngineTests.cs`
- Modify: `MacroObservationEngine.cs`

**Interfaces:**
- Consumes: legal full-12 `ExpertSnapshot` rows matched to revealed `ClosedResult` rows and P5 dependency edges.
- Produces: per-expert 10/20/50/100 Top3, Top6, MRR and MeanRank; pairwise Spearman/Top6 overlap; dependency groups; objective diversity and unique-rescue diagnostics.

- [x] **Step 1: Add hand-calculated failing tests** for dynamic expert pools, short windows, dependency groups, shared relations, consumption edges, and revision isolation.
- [x] **Step 2: Run the P7 smoke test and confirm the statistical assertions fail for missing behavior.**
- [x] **Step 3: Implement common-sample window metrics, pair metrics, group construction, and expert observations.**
- [x] **Step 4: Re-run P7 smoke tests and confirm all dynamic-N cases pass.**

### Task 3: Environment facts, explicit missingness, and phase isolation

**Files:**
- Modify: `Tests/MacroObservationEngineTests.cs`
- Modify: `MacroObservationEngine.cs`
- Create: `docs/P7-Macro-Observation-验收报告.md`

**Interfaces:**
- Consumes: revealed zodiac sequence in `PastResults`.
- Produces: repeat rates, zodiac concentration, per-zodiac recent frequencies and omission facts; unavailable trend/counterfactual/marginal fields stay `null` with zero samples.

- [x] **Step 1: Add failing tests** for immediate/gap1/gap2 repeat, concentration, omission, unavailable trend facts, unavailable marginal contribution, and unchanged formal V7 output.
- [x] **Step 2: Run P7 smoke tests and verify the new facts fail before implementation.**
- [x] **Step 3: Implement the minimum environment and missingness facts.**
- [x] **Step 4: Run P7 smoke, P5 smoke, Macro contract smoke, full regression, and Release build.**
- [x] **Step 5: Document exact formulas, data limits, missing capabilities, and confirmation that P7 is not wired to production.**
