# Independent learning entrance implementation plan

> Execute inline with executing-plans and test-driven-development. Do not delegate or publish.

**Goal:** A third, separately persisted learning model with a verifiable N → learn N → N+1 lifecycle.

**Architecture:** Independent SQLite connection at experiments/v7-independent-learning-v1/learning.db. Read-only caller-supplied three-base-model rankings, no DatabaseHelper or legacy learning calls. Online softmax rank learner, fixed learning rate 0.05, independent coefficients initialized equally. Versioned immutable predictions and atomic feedback receipts. This phase does not wire daily production or UI.

**Tech Stack:** Existing .NET 10 and System.Data.SQLite; no new dependencies.

## Constraints

Preserve both legacy learners, history, configuration and model formulas. Restore only previous unfinished lifecycle hooks and the earlier shared learning behavior changes identified in b83b7dd. Preserve unrelated working-tree edits. Use local caches and a separate build output to avoid the running desktop binary.

## Tasks

- [x] Add Tests/IndependentLearningTests.cs with a dedicated --independent-learning-smoke switch. First check the new type exists (reflection), run and observe failure before implementation.
- [x] Implement IndependentLearningModel.cs with Predict(input), Learn(issue, actual, previousIssue), ReadState(), ReadPrediction(issue). Inputs have target issue, cutoff, source timestamps, full rankings. Reject duplicate/unknown zodiac, future cutoff, source timestamp after prediction and nonsequential learning.
- [x] Persist schema/model/code version, input hash, coefficients, learning rate, before/after states, ranking and probabilities. Immutable insert or exact-input idempotent return; conflicting same-issue input rejected. No writes to legacy tables.
- [x] Test real SQLite rollback via a trigger aborting audit insertion; retry must increment once. Test persistence across instances, duplicate feedback, conflicting actual, future input, gaps and pending predictions.
- [x] Remove unfinished hooks to OnlineLearningPipeline from legacy runtime, restore prior shared feedback behavior with exact patches. Keep dormant draft source without enabling it. Route obsolete learning smoke switch to independent tests, not tests which train legacy memory.
- [x] Run offline build, independent acceptance and complete existing regression suite. Save evidence and limitations to docs/independent-learning-acceptance.md.

## Calculation contract

For each source s and zodiac z, x=(12-rank)/11. Source weights are softmax(theta); score is their weighted mean of x; probability is softmax(score). Start theta=[0,0,0]. After actual z is known, update theta_s += 0.05*w_s*((x_actual_s-E_p[x_s]) - sum_t(w_t*(x_actual_t-E_p[x_t]))). Clip theta to [-5,5]. Only frozen saved input is used. The audit retains exact parameter states. This learns source trust from prediction error; it does not claim to learn causal reasons for errors.

## Acceptance examples

`Predict(N)` has UsedMemoryVersion=0. `Learn(N, actual, cutoff)` produces version1 and a receipt in one transaction. Duplicate call returns false and leaves serialized state unchanged. `Predict(N+1)` has UsedMemoryVersion=1. Failure inserting receipt leaves version0. Future cutoff or skipping pending N throws InvalidDataException. Synthetic legacy memory/history sentinel database bytes must remain unchanged after all independent actions.
