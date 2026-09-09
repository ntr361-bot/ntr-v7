# P14 Macro Gating Implementation Plan

**Goal:** Implement the existing `IMacroGatingModel` contract as a deterministic, dynamic-N, sidecar-only rank aggregator.

**Architecture:** Validate one frozen snapshot and one decision per included ExpertId, select the declared FOLLOW or COUNTER view, then aggregate complete 12-zodiac ranks with non-negative approved weights. The engine has no data access, learning, persistence or production integration.

## Constraints

- Do not change V7/V6.5 algorithms, production Top6, daily workflow or prediction history.
- Do not calculate or approve weights; consume only `AppliedWeight` values already present in the frozen inputs.
- Require exact ExpertId and ExpertRevisionId alignment.
- Reject missing, duplicate or malformed complete-12 rankings.
- COUNTER may only consume an explicit, matching, immutable CounterView; it may not create or infer one.
- Output must be deterministic and independent of input enumeration order.

## Tasks

- [x] Add failing tests for dynamic N, rank aggregation, deterministic ties, mode/view selection and invalid input rejection.
- [x] Confirm tests fail because `MacroGatingModel` is absent.
- [x] Implement `MacroGatingModel` with weighted Borda aggregation and stable zodiac tie-break.
- [x] Verify P14 focused tests and P5-P13 focused suites.
- [x] Run full regression and Release build.
- [x] Update unified acceptance report and stop before P15.
