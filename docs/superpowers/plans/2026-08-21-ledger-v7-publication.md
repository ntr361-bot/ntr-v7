# Ledger V7 Publication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore the failed prediction publication and make generated V7 results visible in the smart ledger.

**Architecture:** Keep the daily V6.5 result payload unchanged, generate the two V7 records alongside it, export them in the existing runtime-state payload, and have the ledger API normalize those records into a compact V7 display block.

**Tech Stack:** C#/.NET prediction runner, Next.js/React TypeScript smart ledger, GitHub Actions.

---

### Task 1: Restore the blocked runner test suite

**Files:**
- Modify: `Form1.cs`
- Modify: `Tests/Program.cs`

- [ ] Change the synchronization label to call downloaded prediction files a cache.
- [ ] Update the model-visibility expectation to match the active 100-period V6.5 display policy.
- [ ] Run the smoke-test project and confirm the former two failures are absent.

### Task 2: Generate and publish V7 daily records

**Files:**
- Modify: `DailyPredictionAutomation.cs`
- Modify: `Tests/Program.cs`

- [ ] Add a failing test that verifies a daily generation stores V7 and V7 AutoLearning records.
- [ ] Generate those records after the V6.5 base snapshots exist.
- [ ] Run the smoke-test project and confirm the new test passes.

### Task 3: Expose V7 results in the ledger

**Files:**
- Modify: `app/api/predictions/route.ts`
- Modify: `app/MobileApp.tsx`
- Modify: `tests/automatic-learning-ui.test.mjs`

- [ ] Add a failing source-level UI test for a dedicated V7 display section.
- [ ] Normalize runtime-state V7 records into the prediction API response.
- [ ] Render V7 and V7 AutoLearning top-three, top-six, and focus numbers below the existing daily prediction.
- [ ] Run ledger tests and production build.
