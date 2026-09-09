# P14 Macro Gating Design

P14 implements the already-frozen `IMacroGatingModel` contract. It is a pure sidecar rank aggregator, not a weight learner, action approver, persistence service or production adapter.

## Inputs and support set

The snapshot, decision and weight collections must contain exactly one item for every included ExpertId. Each decision must bind the same ExpertRevisionId and TargetIssue as its snapshot. Applied weights must be finite, non-negative, equal to the corresponding `ExpertDecision.AppliedWeight`, and sum to one. IGNORE requires zero weight. Empty/all-IGNORE pools do not produce a synthetic ranking.

Every participating view is a complete permutation of the fixed 12-zodiac vocabulary. Missing, duplicate, unknown or incomplete zodiac values are rejected. Snapshot generation/availability and mode selection must be visible by the frozen AsOf time; P14 has no API for reading results or production state.

## View selection

FOLLOW consumes the original immutable snapshot. IGNORE contributes no score. COUNTER consumes only an explicit matching `ExpertCounterView`; P14 never creates a counter view and never interprets a negative weight. The V1 implementation can verify only `full12-rank-reversal-v1`, including identity, revision, source hash, time, full ranking and exact reversal. This validation confirms view integrity, not statistical COUNTER eligibility. Upstream validation evidence and later walk-forward/live-shadow stages remain mandatory before any real use.

## Aggregation

P14 V1 uses weighted Borda rank aggregation:

`Score(z) = Σ expertWeight × (13 - expertRank(z))`

Scores are sorted descending. Exact ties use the fixed zodiac order `鼠牛虎兔龙蛇马羊猴鸡狗猪`, making output independent of dictionary, snapshot or decision enumeration order. Expert input ranks and scores are never modified.

## Isolation

The class has no database, network, workflow, prediction-history, formal-model or UI dependency. Its output is an in-memory full-12 ranking. P11 still reports ranking preview and environment similarity as unavailable because their frozen interface has not been changed; P14 does not retroactively bypass the Critic.
