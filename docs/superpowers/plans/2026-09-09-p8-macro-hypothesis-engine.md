# P8 Macro Hypothesis Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a deterministic, prefix-safe generator for the seven approved Macro V1 hypothesis candidates.

**Architecture:** `MacroHypothesisEngine` receives only frozen P7 observations and reasoning memory. It creates content-bound candidate identities and conservative priors without searching evidence, choosing actions, changing rankings, or writing production state.

**Tech Stack:** C# 14 / .NET 10, immutable Macro contracts, SHA-256 canonical hashing, console smoke tests.

## Global Constraints

- Do not implement P9 evidence, P10 counter-evidence, Critic, Confidence, Decision, Gating, COUNTER, weights, storage, UI, cloud publication, or production integration.
- Do not change formal V7/V6.5 algorithms, Top3/Top6, daily workflow, P0-P7 behavior, or the 17-issue experiment.
- Every candidate set contains all seven approved Macro V1 types, including both null explanations.
- Prediction issue N may use only memory trained through N-1 or earlier.

---

### Task 1: Candidate identity and validation

**Files:**
- Create: `Tests/MacroHypothesisEngineTests.cs`
- Modify: `Tests/Program.cs`
- Create: `MacroHypothesisEngine.cs`

**Interfaces:**
- Consumes: `IMacroHypothesisEngine.Build(MacroObservationSnapshot, ReasoningMemorySnapshot)`.
- Produces: deterministic `HypothesisSet` with seven content-bound candidates.

- [x] **Step 1: Write failing tests** for exact vocabulary, mandatory null explanations, deterministic IDs, observation/memory hash binding and future-memory rejection.
- [x] **Step 2: Run `--p8-hypothesis-smoke` and confirm failure because the engine is absent.**
- [x] **Step 3: Implement minimal validation, canonical identity and candidate construction.**
- [x] **Step 4: Re-run the P8 smoke tests and keep identity cases green.**

### Task 2: Conservative prior policy

**Files:**
- Modify: `Tests/MacroHypothesisEngineTests.cs`
- Modify: `MacroHypothesisEngine.cs`

**Interfaces:**
- Consumes: hypothesis reliability entries from `ReasoningMemorySnapshot.Hypotheses`.
- Produces: prior/current probability and prior logit score under `p8-prior-v1`.

- [x] **Step 1: Add failing tests** for missing memory, fewer than 20 matured samples, shrinkage at 20+ samples, bounds and invalid reliability counters.
- [x] **Step 2: Run P8 smoke and confirm the new prior assertions fail, including an integer-overflow boundary found during review.**
- [x] **Step 3: Implement the frozen conservative prior rule only.**
- [x] **Step 4: Re-run P8 smoke and confirm all prior cases pass.**

### Task 3: Isolation and acceptance report

**Files:**
- Modify: `Tests/MacroHypothesisEngineTests.cs`
- Create: `docs/P8-Macro-Hypothesis-验收报告.md`

**Interfaces:**
- Consumes: formal V7 regression fixture.
- Produces: evidence that hypothesis generation cannot alter production ranking.

- [x] **Step 1: Add a regression test** comparing formal V7 Top6 before and after P8 construction.
- [x] **Step 2: Run P8, P7, P6, P5 and contract smoke suites plus full smoke regression and Release build.**
- [x] **Step 3: Record acceptance evidence and the non-production boundary.**
- [x] **Step 4: Commit P8 and stop before P9.**
