# V8.2 State Recognition and Zodiac Ranking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an isolated V8.2 experiment that classifies the current historical state, ranks all 12 zodiacs with pairwise learning, adds leakage-safe cross features, and independently backtests colors.

**Architecture:** `MarketStateEngine` describes the observable regime, `ZodiacRankingEngine` learns pairwise order, and `V82StateRouter` combines independent short/medium/long engine scores according to the state. `V82Evaluation` performs one strict walk-forward pass and writes reproducible reports without modifying production prediction history.

**Tech Stack:** C#/.NET WinForms project, dependency-free gradient-boosted decision stumps, JSON/Markdown evidence, existing smoke-test executable.

## Global Constraints

- Never use the target or future draw when creating a feature, state, threshold, or training pair.
- Keep five-element features removed.
- Do not overwrite V6.3/V7 prediction history or replace their UI entry points.
- Treat TOP3/TOP6 as views over one complete 12-zodiac ranking.
- Keep `ColorEngine` independent from zodiac predictions.

---

### Task 1: State recognition contract

**Files:**
- Create: `MarketStateEngine.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Produces: `MarketStateEngine.Detect(IReadOnlyList<HistoryRecord>) -> MarketStateResult`
- `MarketStateResult` exposes `PrimaryState`, `Probabilities`, `Confidence`, and `Evidence`.

- [ ] Write a test asserting four finite probabilities sum to 1 and state detection is unchanged when a future record is appended beyond the selected prefix.
- [ ] Run the smoke tests and confirm failure because `MarketStateEngine` does not exist.
- [ ] Implement four state scores from short-cycle repeats, momentum dispersion, and omission ratios; normalize with Softmax.
- [ ] Run all smoke tests and confirm the state tests pass.

### Task 2: Leakage-safe cross features

**Files:**
- Modify: `FeatureEngine.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Extends `FeatureEngine.FeatureNames` and `ZodiacFeature.ToVector()` with named interaction terms.

- [ ] Write tests for at least eight named cross features, finite values, vector/name dimension equality, and prefix invariance.
- [ ] Run tests and confirm the cross-feature assertions fail.
- [ ] Add omission×momentum, count×filter, repeat×omission, and long×short trend interactions.
- [ ] Run all smoke tests and confirm no five-element field reappears.

### Task 3: Pairwise zodiac ranking

**Files:**
- Create: `ZodiacRankingEngine.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Produces: `ZodiacRankingEngine.Predict(history, minimumTraining) -> ZodiacRankingResult`.
- Result exposes 12 unique `ZodiacRankItem` entries with `Score`, `Probability`, and `Rank`.

- [ ] Write tests asserting 12 unique items, ranks 1..12, probabilities sum to 1, and TOP3/TOP6 equal ranking prefixes.
- [ ] Run tests and confirm failure because the ranking API is missing.
- [ ] Build training pairs from actual-minus-other feature vectors and fit gain-selected boosted stumps.
- [ ] Score current zodiac vectors, apply Softmax, and return stable ordering.
- [ ] Run all smoke tests.

### Task 4: State router

**Files:**
- Create: `V82StateRouter.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Consumes `MarketStateResult`, three `V7PredictionResult` objects, and `ZodiacRankingResult`.
- Produces `V82PredictionResult` with state, full ranking, TOP3, TOP6, and routing weights.

- [ ] Write tests proving each state selects its documented routing profile and output remains a complete normalized ranking.
- [ ] Run tests and confirm failure.
- [ ] Implement auditable state profiles and normalized score fusion.
- [ ] Run all smoke tests.

### Task 5: Independent color walk-forward evaluator

**Files:**
- Create: `ColorBacktestEngine.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Produces `ColorBacktestEngine.Run(history, warmup) -> ColorBacktestReport`.

- [ ] Write a test asserting sample count, valid rates, and no dependency on zodiac fields.
- [ ] Run tests and confirm failure.
- [ ] Implement prior-only rolling color evaluation with main, main+defense, exclusion, and miss-run metrics.
- [ ] Run all smoke tests.

### Task 6: 1213-target strict evaluation and reports

**Files:**
- Create: `D:\projects\V82Evaluation\V82Evaluation.csproj`
- Create: `D:\projects\V82Evaluation\Program.cs`
- Generate: `V8.2 Evaluation Report.md`
- Generate: `V8.2 Evaluation Results.json`

**Interfaces:**
- Reads `site/data/history.json` and production source files by linked compile items.
- Writes state, ranking, color, and baseline metrics.

- [ ] Add evaluator assertions for 1313 valid records, 1213 target predictions, monotonic periods, and no missing ranking rows.
- [ ] Run evaluator and confirm assertions initially fail until all engines are wired.
- [ ] Execute the strict walk-forward run and generate JSON/Markdown evidence.
- [ ] Compare V8.2 against V7 on identical target periods and state whether an advantage exists.

### Task 7: Final verification and external analysis

**Files:**
- Build output: `bin/V7History/六合分析软件.exe`

- [ ] Run all smoke tests and require zero failures.
- [ ] Build the WinForms executable with zero compilation errors.
- [ ] Open the supplied ChatGPT share URL, determine whether it permits continuing the conversation, and submit the compact metrics plus report findings if interactive.
- [ ] If a response is returned, evaluate requested changes against leakage and isolation constraints before implementing another tested iteration.

