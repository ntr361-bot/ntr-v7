# 6.5 Color Auto Learning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add independent, persistent color prediction learning with main-color and dual-color feedback, without affecting zodiac ranking.

**Architecture:** Extend model memory with a bounded color-learning state, make `ColorEngine` consume learned weights and expose feature snapshots, and process color feedback through a dedicated engine. Persist color prediction snapshots in the existing history text fields, then enrich strict chronological evaluation and the latest-50 history rows with actual color and hit results.

**Tech Stack:** C# 13, .NET 10 Windows Forms, System.Data.SQLite, existing smoke-test console.

## Global Constraints

- Public application version remains 6.5.
- Color learning never changes zodiac model weights or zodiac ranking.
- Prediction for issue N may only use draws before N.
- Main-color miss threshold is 5 consecutive issues; dual-color miss threshold is 3 consecutive issues.
- A single color weight changes by no more than 5 percentage points per feedback adjustment.
- Existing draw history and old model memory remain compatible.

---

### Task 1: Color Learning State and Weight Optimizer

**Files:**
- Create: `ColorAutoLearningEngine.cs`
- Modify: `ModelMemory.cs`
- Test: `Tests/Program.cs`

**Interfaces:**
- Produces: `ColorLearningWeights`, `ColorLearningState`, `ColorPredictionFeedback`, `ColorLearningOutcome`, `ColorAutoLearningEngine.ApplyFeedback`.
- Consumes: actual color, predicted main/defense colors, and per-color feature signals.

- [ ] **Step 1: Write failing tests** for normalized weight bounds, at-most-5-point adjustment, main miss 5 trigger, dual miss 3 trigger, and duplicate-issue idempotency.
- [ ] **Step 2: Run smoke tests** and verify compilation/tests fail because the color-learning types are absent.
- [ ] **Step 3: Implement minimal color state and feedback engine** with independent counters and JSON-compatible properties.
- [ ] **Step 4: Run smoke tests** and verify the new tests pass.

### Task 2: Weighted Color Prediction and Online Feedback

**Files:**
- Modify: `ColorEngine.cs`
- Modify: `V7PredictionHistoryService.cs`
- Modify: `DatabaseHelper.cs`
- Test: `Tests/Program.cs`

**Interfaces:**
- `ColorEngine.Predict(history, ColorLearningWeights? weights = null)` returns main, defense, exclusion, probabilities, omissions, and `FeatureSignals`.
- History score details carry `波色排除`, `主`, `防`, `波色权重`, and a JSON feature snapshot.
- `DatabaseHelper.ApplyColorLearningForPrediction(id, actualNumber)` loads one snapshot, applies one feedback, and persists memory.

- [ ] **Step 1: Write failing integration tests** proving learned weights can change ranking, history contains a parseable snapshot, and repeated verification learns color once.
- [ ] **Step 2: Run targeted smoke tests** and verify expected failures.
- [ ] **Step 3: Implement weighted prediction and snapshot serialization.**
- [ ] **Step 4: Invoke color feedback only for rows containing a color snapshot; invalid numbers skip learning.**
- [ ] **Step 5: Run smoke tests** and verify zodiac tests remain unchanged.

### Task 3: Strict Color Backtest and Latest-50 History

**Files:**
- Modify: `AutoLearningEvaluation.cs`
- Modify: `DatabaseHelper.cs`
- Modify: `V7PredictionHistoryService.cs`
- Test: `Tests/Program.cs`

**Interfaces:**
- `AutoLearningValidationRecord` adds main, defense, actual color, main hit, and dual hit.
- `AutoLearningEvaluationResult` adds baseline/learning color metrics.
- `SaveVerifiedValidationPrediction` stores actual number and color review text while retaining zodiac hit fields.

- [ ] **Step 1: Write failing chronological evaluation tests** for color fields, latest-50 count, and no future leakage.
- [ ] **Step 2: Run tests** and verify failure due to missing color results.
- [ ] **Step 3: Train color state once over 2023–2025 in chronological order, then predict/reveal/learn over 2026.**
- [ ] **Step 4: Save main/defense and color hit outcomes in the intelligent prediction history.**
- [ ] **Step 5: Run full smoke tests.**

### Task 4: Real Data Verification and Release Build

**Files:**
- Modify generated: `Auto Learning Evaluation Report.md`
- Modify generated: `Auto Learning Evaluation Results.json`
- Modify runtime database: `bin/V7History/history.db`

**Interfaces:**
- Consumes the active application database selected by the desktop shortcut.
- Produces the latest 50 verified color-history rows and a 6.5 Release executable.

- [ ] **Step 1: Run strict 2023–2025 training and 2026 evaluation** against `bin/V7History` data.
- [ ] **Step 2: Persist exactly the latest 50 validation rows and verify first/last issues.**
- [ ] **Step 3: Run all smoke tests and confirm zero failures.**
- [ ] **Step 4: Build Release to `bin/V7History` and confirm zero compiler errors and product version 6.5.**
