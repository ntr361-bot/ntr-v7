# P15 Macro Uncertainty Controller Plan

**Goal:** Implement the frozen `IMacroUncertaintyController` contract as a deterministic final safety layer over a P13 MacroDecision.

## Rules

- [x] Write failing tests for HOLD/REJECT invariants, low-confidence HOLD, critic caps, confidence scaling, direction preservation and malformed input rejection.
- [x] Confirm the implementation is absent.
- [x] Implement `p15-uncertainty-v1` without data access or production integration.
- [x] Verify P15 and P5-P14 focused suites.
- [x] Run full regression and Release build.
- [x] Document acceptance and stop before P16.

The controller may keep, shrink or cancel the P13 applied movement. It may never enlarge it, reverse its direction, alter proposed weights, invent experts or override a Critic rejection.
