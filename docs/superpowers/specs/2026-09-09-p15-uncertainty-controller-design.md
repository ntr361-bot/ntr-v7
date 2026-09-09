# P15 Macro Uncertainty Controller Design

P15 implements the frozen `IMacroUncertaintyController` contract as the last pure safety layer after P13 and before P14 execution in the runtime design. It reads one immutable `MacroDecision` and returns a new immutable decision; it has no history, database, model, UI, network or production dependency.

## Invariants

- HOLD and REJECT must already have `AppliedWeights == BeforeWeights` and zero action magnitude.
- Critic REJECT always becomes REJECT with zero action, even if an inconsistent upstream decision says APPLY.
- Confidence below 0.60 becomes HOLD.
- Expert support sets cannot change.
- Proposed weights and selected/rejected hypotheses are never changed.
- Every applied movement must remain on the line segment from Before to the P13 Applied value; P15 cannot enlarge or reverse a component.
- All weight sets remain finite, non-negative and sum to one.

## V1 cap

For a valid APPLY decision:

`AllowedMagnitude = min(P13Magnitude, Confidence × min(CriticMaximumMagnitude, VerdictCap))`

where `VerdictCap` is 0.01 for CAUTION and 0.05 for PASS. The applied vector is uniformly interpolated from Before toward the P13 Applied vector, preserving direction and relative proportions. If the allowed magnitude is zero, the result is HOLD.

The formula is versioned as `p15-uncertainty-v1`. It is a deterministic safety cap, not evidence that the confidence score is calibrated. It cannot relax any Critic limit or authorize COUNTER.
