# P9-P13 Reasoning and Decision Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the P9-P13 structured reasoning stages as a deterministic, leakage-safe sidecar ending in an immutable MacroDecision.

**Architecture:** Five focused engines consume frozen records from the preceding stage. Each engine validates its boundary, records missing information, and never reaches into production data or ranking services.

**Tech Stack:** C# 14 / .NET 10, immutable Macro contracts, SHA-256 identities, console smoke tests.

## Global Constraints

- No production integration, Gating, COUNTER, zodiac ranking, prediction-history write, cloud publication or formal model changes.
- P9-P13 use only passed immutable inputs; target/future results are rejected.
- Missing data is explicit and can produce HOLD; it is never converted into support.
- P13 does not create ActionProposal and cannot bypass P11/P12.

---

### Task 1: P9 supporting evidence

**Files:** Create `MacroEvidenceEngine.cs`, create `Tests/MacroReasoningP9P13Tests.cs`, modify `Tests/Program.cs`.

- [x] Write failing P9 tests for declared facts, thresholds, missing inputs, deterministic evidence IDs and leakage rejection.
- [x] Run `--p9-p13-smoke` and confirm the engine is absent.
- [x] Implement `MacroEvidenceEngine` under `p9-evidence-v1`.
- [x] Re-run the P9 tests.

### Task 2: P10 active counter-evidence

**Files:** Create `MacroCounterEvidenceEngine.cs`, modify `Tests/MacroReasoningP9P13Tests.cs`.

- [x] Write failing P10 tests for opposite facts, long-window non-confirmation, null-hypothesis challenge, missing inputs and incomplete-support rejection.
- [x] Run and confirm the new tests fail for the absent engine.
- [x] Implement `MacroCounterEvidenceEngine` under `p10-counter-v1`.
- [x] Re-run P9-P10 tests.

### Task 3: P11 independent critic

**Files:** Create `MacroReasoningCritic.cs`, modify `Tests/MacroReasoningP9P13Tests.cs`.

- [x] Write failing tests for leakage, malformed weights, incomplete counter search, sample/short-window caution, action caps and clean pass.
- [x] Run and confirm failure for the absent critic.
- [x] Implement the ten fixed checks without changing weights.
- [x] Re-run P9-P11 tests.

### Task 4: P12 confidence score

**Files:** Create `MacroConfidenceEngine.cs`, modify `Tests/MacroReasoningP9P13Tests.cs`.

- [x] Write failing tests for bounded components, missing-component handling, incomplete-search zero, critic caps and calibration cap.
- [x] Run and confirm failure for the absent engine.
- [x] Implement `p12-confidence-score-v1`.
- [x] Re-run P9-P12 tests.

### Task 5: P13 decision and unified acceptance

**Files:** Create `MacroDecisionEngine.cs`, modify `Tests/MacroReasoningP9P13Tests.cs`, create `docs/P9-P13-Macro-Reasoning-统一验收报告.md`.

- [x] Write failing tests for no-hypothesis HOLD, incomplete counter HOLD, critic REJECT veto, low-confidence HOLD, HOLD invariants, CAUTION scaling and PASS apply.
- [x] Run and confirm failure for the absent engine.
- [x] Implement `p13-decision-v1` and P7→P13 integration test.
- [x] Run unified focused suites, full regression and Release build; document P9-P13 acceptance.
