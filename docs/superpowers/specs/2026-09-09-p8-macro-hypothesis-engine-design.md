# P8 Macro Hypothesis Engine Design

## Scope

P8 implements a deterministic sidecar `MacroHypothesisEngine`. It consumes a frozen P7 `MacroObservationSnapshot` and the prefix-safe `ReasoningMemorySnapshot`, then creates the seven approved Macro V1 hypothesis candidates. It does not gather evidence, search counter-evidence, calculate confidence, propose weights, rank zodiac signs, write production history, or enter the daily workflow.

Formal V7/V6.5 predictions, P0-P7 behavior, the 17-issue three-expert experiment, cloud publishing, Gating and COUNTER remain unchanged.

## Candidate policy

Every build returns exactly these seven candidates in enum order:

1. `ColdReturnIncreasing`
2. `HotPersistenceIncreasing`
3. `TrendSignalDegrading`
4. `RepeatRegimeIncreasing`
5. `ModelPerformanceShift`
6. `RandomFluctuation`
7. `NoMeaningfulChange`

Generating all candidates avoids hidden threshold selection before P9 evidence exists. `RandomFluctuation` and `NoMeaningfulChange` are mandatory alternatives on every issue. Candidate generation does not mean that a hypothesis is supported or selected.

## Identity and frozen semantics

`HypothesisId` is a deterministic SHA-256 content identity bound to the P8 definition version, target issue, hypothesis type, P7 observation hash and reasoning-memory hash. Repeating the same inputs produces byte-for-byte equivalent candidates. A changed observation or memory produces different IDs and cannot masquerade as the original reasoning context.

Each candidate starts with:

- `CurrentProbability = PriorProbability`;
- `HypothesisScore = logit(PriorProbability)`;
- `Status = Active`;
- empty supporting and counter-evidence IDs;
- a type-specific immutable description;
- `FalsificationRuleVersion = p8-falsification-v1/<type>`;
- `EvaluationDueIssue = TargetIssue + 10`.

P8 does not claim that probability is calibrated. P9-P12 must preserve the distinction between this prior and later evidence/confidence.

## Prior policy

The frozen policy version is `p8-prior-v1`.

- Default prior is `0.50` for all seven hypotheses. This is uncertainty, not a 50% empirical claim.
- A memory estimate may be used only when the matching hypothesis has at least 20 matured observations and a finite `EstimatedReliability` in `[0,1]`.
- Accepted memory estimates are conservatively shrunk toward `0.50` with weight `matured / (matured + 20)`.
- The resulting prior is clamped to `[0.10,0.90]` so historical memory cannot make a candidate certain before current evidence and criticism.

Memory keys are the exact `HypothesisType` names. Missing, insufficient or null reliability uses the default prior. No recent model hit rate is read directly by P8.

## Validation and leakage boundary

P8 rejects:

- null inputs;
- invalid issue/cutoff ordering;
- blank or malformed P7 and memory hashes;
- mismatched P7 pool target issue;
- memory with `LastTrainingIssue >= TargetIssue`;
- negative memory versions or impossible reliability counters;
- non-finite or out-of-range stored reliability.

The engine cannot read `history.db`,开奖结果, current actual zodiac, expert files, network resources, or production prediction services. Its only inputs are the two frozen records passed to `Build`.

## Alternatives

`AlternativeHypothesisIds` contains all seven candidate IDs in deterministic order. This represents the full comparison set rather than a selected subset. Later stages must not treat presence in this list as support.

## Acceptance

P8 tests must prove exact vocabulary, mandatory null explanations, deterministic identity, observation/memory identity binding, conservative memory priors, insufficient-memory fallback, invalid/future-memory rejection, absence of evidence IDs, fixed maturity horizon, and unchanged formal V7 Top6 before/after hypothesis construction.

